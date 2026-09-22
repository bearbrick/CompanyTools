using System.Security.Claims;
using System.Text.Json;
using DbStudio.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

/// <summary>连接的项目归属、凭证和并发验证；虚构地址不会被实际连接。</summary>
internal static class ConnectionChecks
{
    internal static void Run(StudioStore store, ClaimsPrincipal owner, ClaimsPrincipal outsider,
        DesignProject project, DesignProject otherProject, string dataDirectory, Action<string, bool> check)
    {
        const string password = "FixtureOnly-Pass123!";
        var profile = store.SaveConnection(owner, new DatabaseConnection
        {
            ProjectId = project.Id,
            Name = "连接生命周期检查",
            Server = "sql.example.invalid",
            Database = "FixtureDatabase",
            IntegratedSecurity = false,
            UserName = "fixture_account"
        }, password);
        check("Connection profile never returns password", !JsonSerializer.Serialize(profile).Contains(password));
        check("Historical connection defaults to development environment", profile.Environment == DatabaseEnvironments.Development);
        check("Connection is isolated to its project", store.Connections(owner, project.Id).Single().Id == profile.Id);
        Reject<UnauthorizedAccessException>("Other project owner cannot list connections", () => store.Connections(outsider, project.Id), check);
        var moved = ModelJson.Clone(profile);
        moved.ProjectId = otherProject.Id;
        Reject<UnauthorizedAccessException>("Connection ID cannot be reassigned across projects", () => store.SaveConnection(outsider, moved, password), check);
        Reject<InvalidOperationException>("Connection cannot be deleted through another owned project", () => store.DeleteConnection(outsider, otherProject.Id, profile.Id, profile.Revision), check);
        check("Cross-project deletion leaves connection intact", store.Connections(owner, project.Id).Count == 1);

        using var db = new SqliteConnection("Data Source=" + Path.Combine(dataDirectory, "studio.db"));
        db.Open();
        using (var deployment = db.CreateCommand())
        {
            deployment.CommandText = """
                INSERT INTO DatabaseDeployments(Id,ProjectId,ConnectionId,ConnectionName,Server,DatabaseName,ProjectRevision,Scope,Status,ReleaseVersion,StartedAt,CompletedAt,Actor,Detail,Environment,RequestedReleaseVersion)
                VALUES('environment-check',$project,$connection,$name,$server,$database,7,'整库','succeeded',2,$time,$time,'检查','完整校验','development',2)
                """;
            deployment.Parameters.AddWithValue("$project", project.Id);
            deployment.Parameters.AddWithValue("$connection", profile.Id);
            deployment.Parameters.AddWithValue("$name", profile.Name);
            deployment.Parameters.AddWithValue("$server", profile.Server);
            deployment.Parameters.AddWithValue("$database", profile.Database);
            deployment.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            deployment.ExecuteNonQuery();
        }
        check("Matching environment retains verified release status",
            store.DatabaseVersions(owner, project.Id).Single().ReleaseVersion == 2);
        string ProtectedSecret()
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT ProtectedSecret FROM DatabaseConnections WHERE Id=$id";
            command.Parameters.AddWithValue("$id", profile.Id);
            return (string)command.ExecuteScalar()!;
        }
        var encrypted = ProtectedSecret();
        check("SQL password is encrypted in storage", !encrypted.Contains(password) && !encrypted.Contains("fixture_account"));
        var protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dataDirectory, "keys"))).CreateProtector("DbStudio.DatabaseCredentials.v1");
        profile.Name = "更新名称后保留密码";
        profile.Environment = DatabaseEnvironments.Test;
        var oldRevision = profile.Revision;
        profile = store.SaveConnection(owner, profile, "");
        check("Connection rename preserves existing SQL password", new SqlConnectionStringBuilder(protector.Unprotect(ProtectedSecret())).Password == password);
        check("Connection release environment persists", store.Connections(owner, project.Id).Single().Environment == DatabaseEnvironments.Test);
        check("Changing release environment invalidates prior environment status",
            store.DatabaseVersions(owner, project.Id).Single() is { Environment: DatabaseEnvironments.Test, Status: "never", ReleaseVersion: null });
        var invalidEnvironment = ModelJson.Clone(profile);
        invalidEnvironment.Environment = "unknown";
        Reject<InvalidOperationException>("Unknown release environment rejected", () => store.SaveConnection(owner, invalidEnvironment, password), check);
        var stale = ModelJson.Clone(profile);
        stale.Revision = oldRevision;
        Reject<InvalidOperationException>("Stale connection edit rejected", () => store.SaveConnection(owner, stale, password), check);
        Reject<InvalidOperationException>("Stale connection delete rejected", () => store.DeleteConnection(owner, project.Id, profile.Id, oldRevision), check);
        check("Stale delete preserves updated connection", store.Connections(owner, project.Id).Single().Revision == profile.Revision);
        var redirected = ModelJson.Clone(profile);
        redirected.Server = "other.example.invalid";
        Reject<InvalidOperationException>("Changing server cannot reuse hidden password", () => store.SaveConnection(owner, redirected, ""), check);
        check("Project backup excludes connection credentials", !store.ExportProject(owner, project.Id).Contains("fixture_account") && !store.ExportProject(owner, project.Id).Contains(password));
        check("Project audit excludes SQL password", store.Audit(owner, project.Id).All(a => !a.Detail.Contains(password)));

        var login = store.Require(outsider, Permission.Read).Login;
        store.SaveProjectMember(owner, project.Id, login, ProjectAccess.Read | ProjectAccess.Database);
        check("Database member can use assigned connection list", store.Connections(outsider, project.Id).Count == 1);
        Reject<UnauthorizedAccessException>("Database member cannot modify connection settings", () => store.SaveConnection(outsider, profile, password), check);
        Reject<UnauthorizedAccessException>("Database member cannot delete connection settings", () => store.DeleteConnection(outsider, project.Id, profile.Id, profile.Revision), check);
        store.SaveProjectMember(owner, project.Id, login, ProjectAccess.None);
        Reject<UnauthorizedAccessException>("Connection list respects revoked membership", () => store.Connections(outsider, project.Id), check);
        store.DeleteConnection(owner, project.Id, profile.Id, profile.Revision);
        check("Connection deletion removes stored credentials", store.Connections(owner, project.Id).Count == 0);
        Reject<InvalidOperationException>("Deleted connection cannot be revived by stale editor", () => store.SaveConnection(owner, profile, password), check);
        check("Connection deletion records project audit", store.Audit(owner, project.Id).Any(a => a.Action == "删除数据库连接" && a.Detail.Contains(profile.Name)));
    }

    private static void Reject<T>(string name, Action action, Action<string, bool> check) where T : Exception
    {
        try
        {
            action();
        }
        catch (T) { check(name, true); return; }
        throw new Exception("FAIL: " + name + " was allowed");
    }
}
