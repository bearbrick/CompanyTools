using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    /// <summary>幂等升级权限和分享存储。历史项目默认只允许系统管理员，避免迁移时扩大访问范围。</summary>
    private static void InitializeProjectAccess(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE IF NOT EXISTS ProjectMembers (
                ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                Access INTEGER NOT NULL,
                PRIMARY KEY(ProjectId, UserId)
            );
            CREATE TABLE IF NOT EXISTS ProjectShares (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL,
                TokenHash TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                Revoked INTEGER NOT NULL DEFAULT 0
            );
            """);
        // 旧审计无法可靠推导项目归属，仅系统管理员能读取这些历史记录。
        using var columns = Command(db, "SELECT count(*) FROM pragma_table_info('Audit') WHERE name='ProjectId'");
        if (Convert.ToInt32(columns.ExecuteScalar()) == 0)
        {
            Execute(db, "ALTER TABLE Audit ADD COLUMN ProjectId TEXT NOT NULL DEFAULT ''");
        }
    }

    /// <summary>读取项目权限。系统管理员显式拥有全部项目；其他账号必须有独立成员记录。</summary>
    public ProjectAccess AccessTo(ClaimsPrincipal principal, string projectId)
    {
        var actor = Require(principal, Permission.Read);
        using var db = Open();
        return AccessTo(db, actor, projectId);
    }

    private static ProjectAccess AccessTo(SqliteConnection db, SessionUser actor, string projectId)
    {
        if (actor.RoleId == "admin")
        {
            return ProjectAccess.All;
        }
        var value = Scalar(db, "SELECT Access FROM ProjectMembers WHERE ProjectId=$project AND UserId=$user",
            ("$project", projectId), ("$user", actor.Id));
        return int.TryParse(value, out var access) ? (ProjectAccess)access : ProjectAccess.None;
    }

    /// <summary>服务端重新验证全局权限及项目权限；不能用猜测项目 ID 绕过隔离。</summary>
    public SessionUser RequireProject(ClaimsPrincipal principal, string projectId, ProjectAccess access, Permission global = Permission.Read)
    {
        var actor = Require(principal, global | Permission.Read);
        using var db = Open();
        var allowed = AccessTo(db, actor, projectId);
        if ((allowed & access) != access)
        {
            throw new UnauthorizedAccessException("没有此项目的操作权限，请联系项目管理员。");
        }
        return actor;
    }

    /// <summary>项目创建者自动成为项目管理员；仅在创建事务内调用。</summary>
    private static void AddProjectOwner(SqliteConnection db, string projectId, string userId) => Execute(db,
        "INSERT INTO ProjectMembers(ProjectId,UserId,Access) VALUES($project,$user,31)",
        ("$project", projectId), ("$user", userId));

    /// <summary>查看指定项目的成员；不会返回其他项目成员。</summary>
    public List<ProjectMember> ProjectMembers(ClaimsPrincipal principal, string projectId)
    {
        RequireProject(principal, projectId, ProjectAccess.Manage);
        using var db = Open();
        using var cmd = Command(db, """
            SELECT u.Id,u.Login,u.DisplayName,m.Access FROM ProjectMembers m
            JOIN Users u ON u.Id=m.UserId WHERE m.ProjectId=$project ORDER BY u.Login
            """, ("$project", projectId));
        using var rows = cmd.ExecuteReader();
        var members = new List<ProjectMember>();
        while (rows.Read())
        {
            members.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), (ProjectAccess)rows.GetInt32(3)));
        }
        return members;
    }

    /// <summary>按已存在的登录账号添加或调整成员。移除当前管理员自身须由其他管理员操作。</summary>
    public void SaveProjectMember(ClaimsPrincipal principal, string projectId, string login, ProjectAccess access)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Manage);
            if ((access & ~ProjectAccess.All) != 0 || (access != ProjectAccess.None && !access.HasFlag(ProjectAccess.Read)))
            {
                throw new InvalidOperationException("项目成员必须具有查看权限。");
            }
            using var db = Open();
            using var tx = db.BeginTransaction();
            var project = ReadProject(db, projectId);
            var userId = Scalar(db, "SELECT Id FROM Users WHERE Login=$login AND (Enabled=1 OR $remove=1)", ("$login", login.Trim()), ("$remove", access == ProjectAccess.None ? 1 : 0));
            if (userId == "")
            {
                throw new InvalidOperationException("该账号不存在或已停用，请先由系统管理员创建账号。");
            }
            if (userId == actor.Id && actor.RoleId != "admin" && !access.HasFlag(ProjectAccess.Manage))
            {
                throw new InvalidOperationException("请让其他项目管理员调整你的管理权限。");
            }
            if (access == ProjectAccess.None)
            {
                Execute(db, "DELETE FROM ProjectMembers WHERE ProjectId=$project AND UserId=$user", ("$project", projectId), ("$user", userId));
            }
            else
            {
                Execute(db, """
                    INSERT INTO ProjectMembers VALUES($project,$user,$access)
                    ON CONFLICT(ProjectId,UserId) DO UPDATE SET Access=$access
                    """, ("$project", projectId), ("$user", userId), ("$access", (int)access));
            }
            Log(db, actor.DisplayName, "项目成员权限", $"{project.Name} / {login} · {access}", projectId);
            tx.Commit();
        }
    }

    /// <summary>保存项目名称、说明；版本条件更新防止覆盖他人的设计保存。</summary>
    public DesignProject UpdateProject(ClaimsPrincipal principal, string projectId, int revision, string name, string description)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Manage);
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || description.Length > 2000)
            {
                throw new InvalidOperationException("项目名称必填且最多 100 字符，说明最多 2000 字符。");
            }
            using var db = Open();
            using var tx = db.BeginTransaction();
            var project = ReadProject(db, projectId);
            project.Name = name.Trim();
            project.Description = description.Trim();
            if (CommitProject(db, project, revision))
            {
                Log(db, actor.DisplayName, "修改项目信息", project.Name, projectId);
            }
            tx.Commit();
            return project;
        }
    }

    /// <summary>独立创建或重命名模块；重命名同时移动其所有表，保证分组一致。</summary>
    public DesignProject SaveModule(ClaimsPrincipal principal, string projectId, int revision, string? oldName, string name)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Design, Permission.Design);
            name = name.Trim();
            if (name.Length is 0 or > 100)
            {
                throw new InvalidOperationException("模块名称必填且最多 100 字符。");
            }
            using var db = Open();
            using var tx = db.BeginTransaction();
            var project = ReadProject(db, projectId);
            var modules = project.Modules.Concat(project.Tables.Select(t => t.Module)).Distinct().ToList();
            if (modules.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase) && m != oldName))
            {
                throw new InvalidOperationException("模块名称已存在。");
            }
            if (oldName != null && !modules.Contains(oldName))
            {
                throw new InvalidOperationException("模块已不存在，请刷新。");
            }
            // 原名保存不重排模块，也不把历史隐式模块转换成一次设计变更。
            if (oldName == name)
            {
                CommitProject(db, project, revision);
                tx.Commit();
                return project;
            }
            project.Modules = modules.Where(m => m != oldName).Append(name).ToList();
            foreach (var table in project.Tables.Where(t => t.Module == oldName))
            {
                table.Module = name;
            }
            if (CommitProject(db, project, revision))
            {
                Log(db, actor.DisplayName, oldName == null ? "创建模块" : "重命名模块", $"{project.Name} / {name}", projectId);
            }
            tx.Commit();
            return project;
        }
    }

    /// <summary>
    /// 在同一事务中检查并发版本、比较完整设计并提交实际变更。
    /// 比较双方均使用归一化模型，避免旧 JSON 的排版或缺省属性造成虚假版本。
    /// 字段顺序、备注及业务说明属于设计内容，发生变化仍需升级修订号。
    /// </summary>
    /// <returns>实际写入返回 true；内容未变返回 false，调用方不应记录变更审计。</returns>
    private bool CommitProject(SqliteConnection db, DesignProject project, int revision)
    {
        var saved = ReadProject(db, project.Id);
        if (project.Revision != revision || saved.Revision != revision)
        {
            throw new InvalidOperationException("项目已更新，请刷新后重试。本次未覆盖其他修改。");
        }
        // 先验证版本再判等，不能以“内容一致”为由放行过期客户端。
        if (JsonSerializer.Serialize(saved, ModelJson.Options) == JsonSerializer.Serialize(project, ModelJson.Options))
        {
            return false;
        }
        project.Revision++;
        using var update = Command(db, "UPDATE Projects SET Name=$name,Revision=$next,Document=$doc WHERE Id=$id AND Revision=$revision",
            ("$name", project.Name), ("$next", project.Revision), ("$doc", JsonSerializer.Serialize(project, ModelJson.Options)),
            ("$id", project.Id), ("$revision", revision));
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("保存冲突，请刷新后重试。");
        }
        return true;
    }
}
