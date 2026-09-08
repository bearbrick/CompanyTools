using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    private IDataProtector databaseProtector = default!;

    /// <summary>连接配置与项目外键关联，凭证由持久化的数据保护密钥加密。</summary>
    private void InitializeConnections(Microsoft.Data.Sqlite.SqliteConnection db, string directory)
    {
        databaseProtector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")))
            .CreateProtector("DbStudio.DatabaseCredentials.v1");
        Execute(db, """
            CREATE TABLE IF NOT EXISTS DatabaseConnections (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                Document TEXT NOT NULL,
                ProtectedSecret TEXT NOT NULL
            );
            """);
    }

    /// <summary>仅有项目数据库权限的成员可列出连接，返回值不含密码。</summary>
    public List<DatabaseConnection> Connections(ClaimsPrincipal principal, string projectId)
    {
        RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
        using var db = Open();
        using var cmd = Command(db, "SELECT Document FROM DatabaseConnections WHERE ProjectId=$project ORDER BY rowid", ("$project", projectId));
        using var rows = cmd.ExecuteReader();
        var result = new List<DatabaseConnection>();
        while (rows.Read())
        {
            result.Add(JsonSerializer.Deserialize<DatabaseConnection>(rows.GetString(0), ModelJson.Options)!);
        }
        return result;
    }

    /// <summary>配置连接要求项目管理与数据库权限。更新时保留密码仅限服务器、库和账号均未改变。</summary>
    public DatabaseConnection SaveConnection(ClaimsPrincipal principal, DatabaseConnection profile, string password)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, profile.ProjectId, ProjectAccess.Manage | ProjectAccess.Database, Permission.Design);
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100 || string.IsNullOrWhiteSpace(profile.Server)
                || string.IsNullOrWhiteSpace(profile.Database) || new[] { "master", "model", "msdb", "tempdb" }.Contains(profile.Database.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("请填写连接名称、服务器和业务数据库，不允许使用系统库。");
            }
            using var db = Open();
            using var tx = db.BeginTransaction();
            ReadProject(db, profile.ProjectId);
            var previousProject = Scalar(db, "SELECT ProjectId FROM DatabaseConnections WHERE Id=$id", ("$id", profile.Id));
            if (previousProject != "" && previousProject != profile.ProjectId)
            {
                throw new UnauthorizedAccessException("连接不属于当前项目。");
            }
            if (previousProject == "" && profile.Revision != 0)
            {
                throw new InvalidOperationException("连接已被删除，请刷新连接列表后重试。");
            }
            var copy = ModelJson.Clone(profile);
            var connection = new SqlConnectionStringBuilder
            {
                DataSource = copy.Server.Trim(),
                InitialCatalog = copy.Database.Trim(),
                IntegratedSecurity = copy.IntegratedSecurity,
                TrustServerCertificate = copy.TrustServerCertificate,
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                ConnectTimeout = 15,
                ApplicationName = "DB Studio",
                PersistSecurityInfo = false
            };
            if (!copy.IntegratedSecurity)
            {
                if (copy.UserName.Trim() == "")
                {
                    throw new InvalidOperationException("请输入 SQL Server 登录账号。");
                }
                connection.UserID = copy.UserName.Trim();
                if (password == "" && previousProject != "")
                {
                    var old = Credential(principal, copy.ProjectId, copy.Id);
                    if (old.Profile.Server != copy.Server || old.Profile.Database != copy.Database || old.Profile.UserName != copy.UserName || old.Profile.IntegratedSecurity)
                    {
                        throw new InvalidOperationException("更换服务器、数据库或账号后请重新输入密码。");
                    }
                    password = new SqlConnectionStringBuilder(old.ConnectionString).Password;
                }
                if (password == "")
                {
                    throw new InvalidOperationException("请输入 SQL Server 密码。");
                }
                connection.Password = password;
            }
            if (previousProject != "")
            {
                var current = JsonSerializer.Deserialize<DatabaseConnection>(Scalar(db, "SELECT Document FROM DatabaseConnections WHERE Id=$id", ("$id", copy.Id)), ModelJson.Options)!;
                if (current.Revision != copy.Revision)
                {
                    throw new InvalidOperationException("连接已修改，请刷新后重试。");
                }
            }
            copy.Revision++;
            Execute(db, """
                INSERT INTO DatabaseConnections VALUES($id,$project,$document,$secret)
                ON CONFLICT(Id) DO UPDATE SET Document=$document,ProtectedSecret=$secret
                """, ("$id", copy.Id), ("$project", copy.ProjectId), ("$document", JsonSerializer.Serialize(copy, ModelJson.Options)),
                ("$secret", databaseProtector.Protect(connection.ConnectionString)));
            Log(db, actor.DisplayName, "保存数据库连接", copy.Name, copy.ProjectId);
            tx.Commit();
            return copy;
        }
    }

    /// <summary>按读取时的版本删除连接配置；拒绝旧页面覆盖他人修改，不连接或删除实际数据库。</summary>
    public void DeleteConnection(ClaimsPrincipal principal, string projectId, string id, int revision)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Manage | ProjectAccess.Database, Permission.Design);
            using var db = Open();
            using var tx = db.BeginTransaction();
            var document = Scalar(db, "SELECT Document FROM DatabaseConnections WHERE Id=$id AND ProjectId=$project", ("$id", id), ("$project", projectId));
            if (document == "")
            {
                throw new InvalidOperationException("连接不存在或不属于当前项目，请刷新连接列表。");
            }
            var current = JsonSerializer.Deserialize<DatabaseConnection>(document, ModelJson.Options)!;
            if (current.Revision != revision)
            {
                throw new InvalidOperationException("连接已被其他成员修改，请刷新连接列表后重新确认删除。");
            }
            Execute(db, "DELETE FROM DatabaseConnections WHERE Id=$id AND ProjectId=$project", ("$id", id), ("$project", projectId));
            Log(db, actor.DisplayName, "删除数据库连接", $"{current.Name} · 只删除连接配置", projectId);
            tx.Commit();
        }
    }

    /// <summary>后台工具读取凭证前重新核验项目归属，凭证不进入备份、分享或日志。</summary>
    internal DatabaseCredential Credential(ClaimsPrincipal principal, string projectId, string id)
    {
        RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
        using var db = Open();
        using var cmd = Command(db, "SELECT Document,ProtectedSecret FROM DatabaseConnections WHERE Id=$id AND ProjectId=$project", ("$id", id), ("$project", projectId));
        using var row = cmd.ExecuteReader();
        if (!row.Read())
        {
            throw new InvalidOperationException("连接不存在或不属于当前项目。");
        }
        return new(JsonSerializer.Deserialize<DatabaseConnection>(row.GetString(0), ModelJson.Options)!, databaseProtector.Unprotect(row.GetString(1)));
    }

    /// <summary>数据库操作审计不记录连接串、密码和完整 DDL。</summary>
    internal void LogDatabaseOperation(ClaimsPrincipal principal, string projectId, string action, string detail)
    {
        var actor = RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
        using var db = Open();
        Log(db, actor.DisplayName, action, detail, projectId);
    }

    /// <summary>执行结果使用开始时的已授权身份记录，避免操作中途撤权导致成功结果丢失。</summary>
    internal void LogDatabaseResult(string actor, string projectId, string action, string detail)
    {
        using var db = Open();
        Log(db, actor, action, detail, projectId);
    }

    /// <summary>取得经过授权的已保存项目快照供后台工具使用。</summary>
    internal DesignProject DatabaseProject(ClaimsPrincipal principal, string projectId)
    {
        RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
        using var db = Open();
        return ReadProject(db, projectId);
    }

    /// <summary>将反推结构合并到当前项目，保留已有表 ID、模块与业务名称；不删除仅存在于设计的表。</summary>
    public DesignProject ApplyDatabaseSnapshot(ClaimsPrincipal principal, string projectId, int revision, DatabaseSnapshot snapshot)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Database | ProjectAccess.Design, Permission.Design);
            if (snapshot.Warnings.Count > 0)
            {
                throw new InvalidOperationException("反推结果包含尚不能无损维护的特性，请先处理提示项。");
            }
            using var db = Open();
            using var tx = db.BeginTransaction();
            var project = ReadProject(db, projectId);
            var incoming = ModelJson.Clone(snapshot.Project);
            var ids = incoming.Tables.ToDictionary(t => t.Id, t => project.Tables.FirstOrDefault(p => p.Schema.Equals(t.Schema, StringComparison.OrdinalIgnoreCase) && p.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.NewGuid().ToString("N"));
            foreach (var table in incoming.Tables)
            {
                table.Id = ids[table.Id];
                var old = project.Tables.FirstOrDefault(t => t.Id == table.Id);
                if (old != null)
                {
                    table.Module = old.Module;
                    table.Label = old.Label;
                    table.Comment = old.Comment;
                    foreach (var column in table.Columns)
                    {
                        var oldColumn = old.Columns.FirstOrDefault(c => c.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
                        if (oldColumn != null)
                        {
                            column.Id = oldColumn.Id;
                            column.Label = oldColumn.Label;
                            column.Comment = oldColumn.Comment;
                            column.InputLimit = oldColumn.InputLimit;
                        }
                    }
                    project.Tables.Remove(old);
                }
                foreach (var fk in table.ForeignKeys)
                {
                    fk.TargetTableId = ids[fk.TargetTableId];
                }
                project.Tables.Add(table);
            }
            var errors = project.Tables.SelectMany(t => SqlServerDdl.Validate(project, t)).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", errors));
            }
            CommitProject(db, project, revision);
            Log(db, actor.DisplayName, "从数据库反推设计", $"{project.Name} · {incoming.Tables.Count} 张表", projectId);
            tx.Commit();
            return project;
        }
    }
}
