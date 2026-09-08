using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DbStudio.Core;

/// <summary>
/// 项目文档的读取、保存与导出。事务、版本号和入站外键校验共同保护设计完整性。
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>
    /// 读取当前账号可查看的全部项目，并归一化历史导入类型。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    public List<DesignProject> Projects(ClaimsPrincipal principal)
    {
        var actor = Require(principal, Permission.Read);
        using var db = Open();
        using var cmd = Command(db, """
            SELECT p.Document FROM Projects p WHERE $admin=1 OR EXISTS (
                SELECT 1 FROM ProjectMembers m WHERE m.ProjectId=p.Id AND m.UserId=$user AND (m.Access & 1)=1
            ) ORDER BY p.rowid
            """, ("$admin", actor.RoleId == "admin" ? 1 : 0), ("$user", actor.Id));
        using var rows = cmd.ExecuteReader();
        var result = new List<DesignProject>();
        while (rows.Read())
        {
            result.Add(ModelJson.NormalizeImport(JsonSerializer.Deserialize<DesignProject>(rows.GetString(0), ModelJson.Options)!));
        }

        return result;
    }

    /// <summary>
    /// 从指定连接读取项目快照；缺失时提示刷新，不创建空白替代数据。
    /// </summary>
    private DesignProject ReadProject(SqliteConnection db, string id)
    {
        var json = Scalar(db, "SELECT Document FROM Projects WHERE Id=$id", ("$id", id));
        if (json == "")
        {
            throw new InvalidOperationException("项目不存在，请刷新。");
        }

        return ModelJson.NormalizeImport(JsonSerializer.Deserialize<DesignProject>(json, ModelJson.Options)!);
    }

    /// <summary>
    /// 校验创建权限与名称，并在同一事务中保存项目和审计。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="name">项目显示名称。</param>
    /// <param name="description">项目用途说明。</param>
    public DesignProject NewProject(ClaimsPrincipal principal, string name, string description)
    {
        lock (gate)
        {
            var actor = Require(principal, Permission.Projects);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            {
                throw new InvalidOperationException("项目名称必填且不能超过 100 字符。");
            }

            var project = new DesignProject { Name = name.Trim(), Description = description, Revision = 1 };
            using var db = Open();
            using var tx = db.BeginTransaction();
            Execute(
                db,
                "INSERT INTO Projects VALUES($id,$name,1,$doc)",
                ("$id", project.Id),
                ("$name", project.Name),
                ("$doc", JsonSerializer.Serialize(project, ModelJson.Options)));
            AddProjectOwner(db, project.Id, actor.Id);
            Log(db, actor.DisplayName, "新建项目", project.Name, project.Id);
            tx.Commit();
            return project;
        }
    }

    /// <summary>
    /// 保存或删除一张表；校验项目版本及双向引用，任何失败均回滚整个事务。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="projectId">目标项目稳定标识。</param>
    /// <param name="revision">客户端读取时的修订号，用于检测并发修改。</param>
    /// <param name="table">待处理的表设计快照。</param>
    /// <param name="delete">是否删除设计定义，不操作实际业务数据库。</param>
    public DesignProject SaveTable(ClaimsPrincipal principal, string projectId, int revision, TableDesign table, bool delete = false)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Design, Permission.Design);
            using var db = Open();
            using var tx = db.BeginTransaction();
            // 进程内锁串行化写操作；修订号继续保护不同会话和不同进程的旧快照。
            var project = ReadProject(db, projectId);
            if (project.Revision != revision)
            {
                throw new InvalidOperationException("项目已被其他会话更新。请先复制你的修改，再刷新项目后重试；本次未覆盖他人内容。");
            }

            var old = project.Tables.FindIndex(t => t.Id == table.Id);
            if (delete)
            {
                if (old < 0)
                {
                    throw new InvalidOperationException("数据表不存在。");
                }

                if (project.Tables.Any(t => t.Id != table.Id && t.ForeignKeys.Any(f => f.TargetTableId == table.Id)))
                {
                    throw new InvalidOperationException("此表仍被其他表的外键引用，请先解除引用。");
                }

                project.Tables.RemoveAt(old);
            }
            else
            {
                var copy = ModelJson.Clone(table);
                if (old < 0)
                {
                    project.Tables.Add(copy);
                }
                else
                {
                    project.Tables[old] = copy;
                }

                // 父表的主键或字段类型改变时，也必须验证其他表指向它的外键。
                var errors = SqlServerDdl.ValidateChange(project, copy);

                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(string.Join("\n", errors));
                }
            }
            // 无实际变化时不写文档、不增加版本，也不产生虚假的变更记录。
            if (CommitProject(db, project, revision))
            {
                Log(
                    db,
                    actor.DisplayName,
                    delete ? "删除表" : old < 0 ? "新增表" : "保存设计",
                    $"{project.Name} / {table.Schema}.{table.Name} · r{project.Revision}", projectId);
            }
            tx.Commit();
            return project;
        }
    }

    /// <summary>
    /// 校验导出权限，返回已保存项目的完整 JSON 备份。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="id">目标项目或用户的稳定标识。</param>
    public string ExportProject(ClaimsPrincipal principal, string id)
    {
        RequireProject(principal, id, ProjectAccess.Export, Permission.Export);
        using var db = Open();
        return JsonSerializer.Serialize(ReadProject(db, id), ModelJson.Options);
    }

    /// <summary>
    /// 校验导出权限并生成当前编辑快照的建表脚本；不执行数据库命令。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="project">包含关联表的项目设计快照。</param>
    /// <param name="table">待处理的表设计快照。</param>
    public string Ddl(ClaimsPrincipal principal, DesignProject project, TableDesign table)
    {
        RequireProject(principal, project.Id, ProjectAccess.Export, Permission.Export);
        return SqlServerDdl.Generate(project, table);
    }
}
