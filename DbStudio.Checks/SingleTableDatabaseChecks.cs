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
        check("SQL comparison exposes measured stages", freshPlan.Timings is { Count: 4 } && freshPlan.Timings.All(t => t.Milliseconds >= 0));
        project = store.SaveTable(admin, project.Id, project.Revision, fresh, delete: true);
        var initial = await tools.CompareAsync(admin, project.Id, profile.Id);
        await tools.ExecuteAsync(admin, project.Id, initial.Id, database);
        a.Columns[1].Length = "100";
        a.Columns.Add(new() { Name = "Added", Type = "int", Default = "7", Nullable = false });
        b.Columns.Add(new() { Name = "MustNotDeploy", Type = "int" });
        project = store.SaveTable(admin, project.Id, project.Revision, a);
        project = store.SaveTable(admin, project.Id, project.Revision, b);
        var plan = await tools.CompareTableAsync(admin, project.Id, profile.Id, a.Id);
        check("SQL single-table plan carries explicit table scope", plan.Scope?.TableId == a.Id && plan.Changes.Count > 0);
        check("SQL single-table script excludes unselected pending fields", !plan.Script.Contains("MustNotDeploy"));
        await tools.ExecuteAsync(admin, project.Id, plan.Id, database);
        var actual = await tools.ReverseAsync(admin, project.Id, profile.Id);
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
        Console.WriteLine("SINGLE TABLE SQL TEST: " + server + " / " + database);
    }
}
