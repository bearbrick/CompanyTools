using System.Security.Claims;
using DbStudio.Core;
using Microsoft.Data.SqlClient;

/// <summary>可选的真实 SQL Server 集成检查，仅创建并使用本次专属测试数据库。</summary>
internal static class DatabaseChecks
{
    internal static async Task RunAsync(StudioStore store, ClaimsPrincipal admin, string server, Action<string, bool> check)
    {
        var database = "DbStudioIntegration_" + Guid.NewGuid().ToString("N")[..12];
        var connectionString = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true }.ConnectionString;
        using (var master = new SqlConnection(connectionString))
        {
            await master.OpenAsync();
            using var create = master.CreateCommand();
            create.CommandText = "CREATE DATABASE " + SqlServerDdl.Q(database);
            await create.ExecuteNonQueryAsync();
        }
        var project = store.NewProject(admin, "SQL Server 集成测试", "专属测试库 " + database);
        var profile = store.SaveConnection(admin, new DatabaseConnection { ProjectId = project.Id, Server = server, Database = database, Name = "LocalDB 集成检查", TrustServerCertificate = true }, "");
        var tools = new SqlServerTools(store);
        check("SQL Server connection test", (await tools.TestAsync(admin, project.Id, profile.Id)).Contains(database));
        var category = new TableDesign
        {
            Name = "Category",
            Label = "分类",
            PrimaryKeyName = "PK_Category_Custom",
            PrimaryKeyClustered = false,
            Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }, new() { Name = "Name", Type = "nvarchar", Length = "40", Nullable = false }],
            Indexes = [new() { Name = "IX_Category_Name", Columns = "Name", DescendingColumns = "Name", Unique = true, Clustered = true }]
        };
        var product = new TableDesign
        {
            Name = "Product",
            Label = "产品",
            Columns = [new() { Name = "Id", Type = "bigint", Nullable = false, PrimaryKeyOrder = 1, Identity = true },
            new() { Name = "CategoryId", Type = "int", Nullable = true }, new() { Name = "Price", Type = "decimal", Precision = 12, Scale = 2, Nullable = false, Default = "0" },
            new() { Name = "CreatedAt", Type = "datetime2", TemporalScale = 3, Default = "sysdatetime()", Nullable = false }],
            ForeignKeys = [new() { Name = "FK_Product_Category", Columns = "CategoryId", TargetTableId = category.Id, TargetColumns = "Id", OnDelete = "SET NULL" }],
            Checks = [new() { Name = "CK_Product_Price", Expression = "[Price]>=0" }],
            Indexes = [new() { Name = "IX_Product_Category", Columns = "CategoryId", Include = "Price", Filter = "[CategoryId] IS NOT NULL" }]
        };
        project = store.SaveTable(admin, project.Id, project.Revision, category);
        project = store.SaveTable(admin, project.Id, project.Revision, product);
        check("SQL Server design package builds", SqlServerTools.BuildPackage(project).Length > 0);
        var plan = await tools.CompareAsync(admin, project.Id, profile.Id);
        check("SQL Server initial diff has create operations", plan.Changes.Any(c => c.Name.Contains("Product")) && plan.Script.Contains("CREATE TABLE"));
        check("SQL Server compare does not write target", (await tools.ReverseAsync(admin, project.Id, profile.Id)).Project.Tables.Count == 0);
        await RejectAsync("SQL Server requires exact database confirmation", () => tools.ExecuteAsync(admin, project.Id, plan.Id, "wrong"), check);
        await tools.ExecuteAsync(admin, project.Id, plan.Id, database);
        var snapshot = await tools.ReverseAsync(admin, project.Id, profile.Id);
        check("SQL Server deployment round trip preserves tables", snapshot.Project.Tables.Count == 2 && snapshot.Warnings.Count == 0);
        var actualCategory = snapshot.Project.Tables.Single(t => t.Name == "Category");
        var actualProduct = snapshot.Project.Tables.Single(t => t.Name == "Product");
        check("SQL Server reverse preserves custom PK and descending index", actualCategory.PrimaryKeyName == category.PrimaryKeyName && actualCategory.PrimaryKeyClustered == false && actualCategory.Indexes.Single().DescendingColumns == "Name");
        check("SQL Server reverse preserves FK CHECK precision", actualProduct.ForeignKeys.Single().OnDelete == "SET NULL" && actualProduct.Checks.Count == 1 && actualProduct.Columns.Single(c => c.Name == "CreatedAt").TemporalScale == 3);
        await RejectAsync("SQL Server consumed plan cannot repeat", () => tools.ExecuteAsync(admin, project.Id, plan.Id, database), check);
        var same = await tools.CompareAsync(admin, project.Id, profile.Id);
        check("SQL Server synchronized design has no diff", same.Changes.Count == 0);

        var targetConnection = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;
        using (var target = new SqlConnection(targetConnection))
        {
            await target.OpenAsync();
            using var insert = target.CreateCommand();
            insert.CommandText = "INSERT dbo.Category VALUES(1,N'测试分类'); INSERT dbo.Product(CategoryId,Price) VALUES(1,12.50);";
            await insert.ExecuteNonQueryAsync();
        }
        product.Columns.Add(new()
        {
            Name = "Quantity",
            Type = "int",
            Nullable = false,
            Default = "1"
        });
        project = store.SaveTable(admin, project.Id, project.Revision, product);
        var update = await tools.CompareAsync(admin, project.Id, profile.Id);
        check("SQL Server incremental diff adds column", update.Script.Contains("Quantity"));
        await tools.ExecuteAsync(admin, project.Id, update.Id, database);
        using (var target = new SqlConnection(targetConnection))
        {
            await target.OpenAsync();
            using var read = target.CreateCommand();
            read.CommandText = "SELECT Quantity FROM dbo.Product WHERE Id=1";
            check("SQL Server incremental sync preserves business data", Convert.ToInt32(await read.ExecuteScalarAsync()) == 1);
        }
        var stale = await tools.CompareAsync(admin, project.Id, profile.Id);
        project = store.UpdateProject(admin, project.Id, project.Revision, project.Name, "新版本");
        await RejectAsync("SQL Server stale project plan rejected", () => tools.ExecuteAsync(admin, project.Id, stale.Id, database), check);
        var drift = await tools.CompareAsync(admin, project.Id, profile.Id);
        using (var target = new SqlConnection(targetConnection))
        {
            await target.OpenAsync();
            using var change = target.CreateCommand();
            change.CommandText = "ALTER TABLE dbo.Product ADD ExternalNote nvarchar(20) NULL";
            await change.ExecuteNonQueryAsync();
        }
        await RejectAsync("SQL Server target drift rejected", () => tools.ExecuteAsync(admin, project.Id, drift.Id, database), check);
        var reverse = await tools.ReverseAsync(admin, project.Id, profile.Id);
        var oldId = project.Tables.Single(t => t.Name == "Product").Id;
        project = store.ApplyDatabaseSnapshot(admin, project.Id, project.Revision, reverse);
        check("SQL Server reverse merges by table identity", project.Tables.Single(t => t.Name == "Product").Id == oldId && project.Tables.Single(t => t.Name == "Product").Columns.Any(c => c.Name == "ExternalNote"));
        using (var target = new SqlConnection(targetConnection))
        {
            await target.OpenAsync();
            using var extra = target.CreateCommand();
            extra.CommandText = "CREATE TABLE dbo.ExtraTable(Id int NOT NULL); INSERT dbo.ExtraTable VALUES(99); EXEC(N'CREATE VIEW dbo.KeepView AS SELECT Id FROM dbo.Product');";
            await extra.ExecuteNonQueryAsync();
        }
        var preserve = await tools.CompareAsync(admin, project.Id, profile.Id);
        check("SQL Server default comparison preserves unmanaged objects", !preserve.Changes.Any(c => c.Operation == "Drop"));
        var blocked = false;
        try
        {
            var protectedDrop = await tools.CompareAsync(admin, project.Id, profile.Id, prune: true);
            await tools.ExecuteAsync(admin, project.Id, protectedDrop.Id, database);
        }
        catch (Microsoft.SqlServer.Dac.DacServicesException) { blocked = true; }
        check("SQL Server blocks destructive change with existing rows by default", blocked);
        using (var target = new SqlConnection(targetConnection))
        {
            await target.OpenAsync();
            using var verify = target.CreateCommand();
            verify.CommandText = "SELECT Id FROM dbo.ExtraTable";
            check("SQL Server blocked deployment preserves target data", Convert.ToInt32(await verify.ExecuteScalarAsync()) == 99);
        }
        var allowedDrop = await tools.CompareAsync(admin, project.Id, profile.Id, prune: true, allowDataLoss: true);
        await tools.ExecuteAsync(admin, project.Id, allowedDrop.Id, database);
        using (var target = new SqlConnection(targetConnection))
        {
            await target.OpenAsync();
            using var verify = target.CreateCommand();
            verify.CommandText = "SELECT CASE WHEN OBJECT_ID('dbo.ExtraTable') IS NULL AND OBJECT_ID('dbo.KeepView') IS NOT NULL THEN 1 ELSE 0 END";
            check("SQL Server explicit prune drops extra table and preserves view", Convert.ToInt32(await verify.ExecuteScalarAsync()) == 1);
        }
        var deletedConnectionPlan = await tools.CompareAsync(admin, project.Id, profile.Id);
        store.DeleteConnection(admin, project.Id, profile.Id, profile.Revision);
        await RejectAsync("SQL Server plan cannot use deleted connection", () => tools.ExecuteAsync(admin, project.Id, deletedConnectionPlan.Id, database), check);
        using (var target = new SqlConnection(targetConnection))
        {
            await target.OpenAsync();
            using var verify = target.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM dbo.Product";
            check("Deleting connection config leaves actual database intact", Convert.ToInt32(await verify.ExecuteScalarAsync()) == 1);
        }
        Console.WriteLine("SQL INTEGRATION DATABASE: " + server + " / " + database);
    }

    private static async Task RejectAsync(string name, Func<Task> action, Action<string, bool> check)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException) { check(name, true); return; }
        throw new Exception("FAIL: " + name + " was allowed");
    }
}
