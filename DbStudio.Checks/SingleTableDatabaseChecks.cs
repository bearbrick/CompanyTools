using System.Security.Claims;
using DbStudio.Core;
using Microsoft.Data.SqlClient;

/// <summary>在本机专属测试数据库验证单表计划的真实部署、隔离与再比对，不使用项目业务连接。</summary>
internal static class SingleTableDatabaseChecks
{
    internal static async Task RunAsync(StudioStore store, ClaimsPrincipal admin, string server, Action<string, bool> check)
    {
        var database = "DbStudioSingleTable_" + Guid.NewGuid().ToString("N")[..12];
        var connectionString = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true }.ConnectionString;
        using (var master = new SqlConnection(connectionString))
        {
            await master.OpenAsync();
            using var command = master.CreateCommand();
            command.CommandText = "CREATE DATABASE " + SqlServerDdl.Q(database);
            await command.ExecuteNonQueryAsync();
        }
        var project = store.NewProject(admin, "单表部署集成验证", database);
        var profile = store.SaveConnection(admin, new DatabaseConnection { ProjectId = project.Id, Name = "专属本机测试库", Server = server, Database = database, TrustServerCertificate = true }, "");
        var a = new TableDesign { Name = "Selected", Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }, new() { Name = "Value", Type = "nvarchar", Length = "40" }] };
        var b = new TableDesign { Name = "Unselected", Columns = [new() { Name = "Id", Type = "int" }] };
        project = store.SaveTable(admin, project.Id, project.Revision, a);
        project = store.SaveTable(admin, project.Id, project.Revision, b);
        var tools = new SqlServerTools(store);
        // 新表的匿名默认约束在 DacFx 报告中带类型前缀，也必须正确识别其表范围。
        var fresh = new TableDesign { Name = "Fresh", Columns = [new() { Name = "Value", Type = "int", Default = "0" }] };
        project = store.SaveTable(admin, project.Id, project.Revision, fresh);
        var freshPlan = await tools.CompareTableAsync(admin, project.Id, profile.Id, fresh.Id);
        check("SQL single-table new table accepts anonymous default report names", freshPlan.Changes.Any(c => c.ObjectType == "SqlDefaultConstraint"));
        check("SQL comparison exposes measured stages", freshPlan.Timings is { Count: >= 4 }
            && freshPlan.Timings.All(t => t.Milliseconds >= 0)
            && freshPlan.Timings.Any(t => t.Stage == "核验类型容量与数据风险"));
        // 复现单表仍不存在，但预览后其他表发生变化的场景；同步不得误拦或覆盖那张表。
        using (var concurrent = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString))
        {
            await concurrent.OpenAsync();
            using var command = concurrent.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.UnrelatedDuringPreview (Id int NULL)";
            await command.ExecuteNonQueryAsync();
        }
        await tools.ExecuteAsync(admin, project.Id, freshPlan.Id, database);
        var created = await tools.ReverseAsync(admin, project.Id, profile.Id);
        check("SQL single-table initial deployment really creates the requested table", created.Project.Tables.Any(t => t.Name == fresh.Name));
        check("SQL missing-table deployment preserves unrelated concurrent table creation", created.Project.Tables.Any(t => t.Name == "UnrelatedDuringPreview"));
        var freshAgain = await tools.CompareTableAsync(admin, project.Id, profile.Id, fresh.Id);
        check("SQL newly created single table has zero differences", freshAgain.Changes.Count == 0);
        // 只清理本测试刚创建的表，后续继续验证双表范围隔离。
        using (var cleanup = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString))
        {
            await cleanup.OpenAsync();
            using var command = cleanup.CreateCommand();
            command.CommandText = "DROP TABLE dbo.Fresh; DROP TABLE dbo.UnrelatedDuringPreview;";
            await command.ExecuteNonQueryAsync();
        }
        project = store.SaveTable(admin, project.Id, project.Revision, fresh, delete: true);
        var initial = await tools.CompareAsync(admin, project.Id, profile.Id);
        await tools.ExecuteAsync(admin, project.Id, initial.Id, database);
        // 在独立测试库建立安全对象，核对单表同步及清理不会改变用户 SID、角色和授权。
        using (var security = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString))
        {
            await security.OpenAsync();
            using var command = security.CreateCommand();
            command.CommandText = """
                CREATE USER [RegressionReader] WITHOUT LOGIN;
                CREATE ROLE [RegressionRole];
                ALTER ROLE [RegressionRole] ADD MEMBER [RegressionReader];
                GRANT SELECT ON dbo.Selected TO [RegressionRole];
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                CREATE TRIGGER [RegressionDdlObserver] ON DATABASE FOR CREATE_INDEX
                AS INSERT dbo.Unselected(Id) VALUES(9137);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var securityBefore = await SecurityStateAsync();
        a.Columns[1].Length = "100";
        a.Columns.Add(new() { Name = "Added", Type = "int", Default = "7", Nullable = false });
        a.Indexes.Add(new() { Name = "IX_Selected_Value", Columns = "Value" });
        b.Columns.Add(new() { Name = "MustNotDeploy", Type = "int" });
        project = store.SaveTable(admin, project.Id, project.Revision, a);
        project = store.SaveTable(admin, project.Id, project.Revision, b);
        var plan = await tools.CompareTableAsync(admin, project.Id, profile.Id, a.Id);
        check("SQL single-table plan carries explicit table scope", plan.Scope?.TableId == a.Id && plan.Changes.Count > 0);
        check("SQL single-table script excludes unselected pending fields", !plan.Script.Contains("MustNotDeploy"));
        await tools.ExecuteAsync(admin, project.Id, plan.Id, database);
        var actual = await tools.ReverseAsync(admin, project.Id, profile.Id);
        check("SQL single-table execution creates the ordinary index", actual.Project.Tables.Single(t => t.Name == a.Name)
            .Indexes.Any(i => i.Name == "IX_Selected_Value" && !i.Unique && !i.Clustered));
        check("SQL single-table execution applies only selected field changes", actual.Project.Tables.Single(t => t.Name == a.Name).Columns.Any(c => c.Name == "Added")
            && actual.Project.Tables.Single(t => t.Name == a.Name).Columns.Single(c => c.Name == "Value").Length == "100"
            && actual.Project.Tables.Single(t => t.Name == b.Name).Columns.All(c => c.Name != "MustNotDeploy"));
        var same = await tools.CompareTableAsync(admin, project.Id, profile.Id, a.Id);
        check("SQL single-table second comparison has zero differences", same.Changes.Count == 0);
        var full = await tools.CompareAsync(admin, project.Id, profile.Id);
        check("SQL whole-project comparison still exposes unselected pending work", full.Script.Contains("MustNotDeploy"));
        a.Columns.RemoveAll(c => c.Name == "Added");
        project = store.SaveTable(admin, project.Id, project.Revision, a);
        var prune = await tools.CompareTableAsync(admin, project.Id, profile.Id, a.Id, prune: true);
        check("SQL single-table prune has a column removal without other table drops", prune.Changes.Any(c => c.Operation == "Drop") && !prune.Script.Contains("DROP TABLE [dbo].[Unselected]"));
        await tools.ExecuteAsync(admin, project.Id, prune.Id, database);
        actual = await tools.ReverseAsync(admin, project.Id, profile.Id);
        check("SQL single-table prune preserves unselected table", actual.Project.Tables.Count == 2 && actual.Project.Tables.Single(t => t.Name == a.Name).Columns.All(c => c.Name != "Added"));
        check("SQL single-table sync and prune preserve users roles permissions and SIDs", await SecurityStateAsync() == securityBefore);
        using (var observer = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString))
        {
            await observer.OpenAsync();
            using var command = observer.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM dbo.Unselected WHERE Id=9137";
            check("SQL database DDL trigger remains active during index deployment", Convert.ToInt32(await command.ExecuteScalarAsync()) > 0);
        }
        Console.WriteLine("SINGLE TABLE SQL TEST: " + server + " / " + database);

        async Task<string> SecurityStateAsync()
        {
            using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (
                    SELECT p.name, p.type_desc, p.authentication_type_desc, CONVERT(varchar(172), p.sid, 2) AS sid,
                        r.name AS role_name, dp.permission_name, dp.state_desc
                    FROM sys.database_principals p
                    LEFT JOIN sys.database_role_members m ON m.member_principal_id = p.principal_id
                    LEFT JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
                    LEFT JOIN sys.database_permissions dp ON dp.grantee_principal_id = p.principal_id
                    WHERE p.name IN ('RegressionReader', 'RegressionRole')
                    ORDER BY p.name, r.name, dp.permission_name, dp.state_desc
                    FOR JSON PATH
                )
                """;
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }
}
