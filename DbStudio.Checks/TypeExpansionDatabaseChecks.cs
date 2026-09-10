using System.Security.Claims;
using DbStudio.Core;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

/// <summary>在专属临时数据库中验证有数据时的扩容、拦截和往返一致性，不使用业务连接。</summary>
internal static class TypeExpansionDatabaseChecks
{
    internal static async Task RunAsync(StudioStore store, ClaimsPrincipal admin, string server, Action<string, bool> check)
    {
        var database = "DbStudioExpansion_" + Guid.NewGuid().ToString("N")[..12];
        var builder = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true };
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync();
        using (var create = master.CreateCommand())
        {
            create.CommandText = "CREATE DATABASE " + SqlServerDdl.Q(database) + " COLLATE Chinese_PRC_CI_AS";
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            builder.InitialCatalog = database;
            await using var target = new SqlConnection(builder.ConnectionString);
            await target.OpenAsync();
            var project = store.NewProject(admin, "类型扩容测试", database);
            var profile = store.SaveConnection(admin, new DatabaseConnection { ProjectId = project.Id, Server = server, Database = database, Name = "独立扩容测试", TrustServerCertificate = true }, "");
            var tools = new SqlServerTools(store);
            var table = new TableDesign
            {
                Name = "Expansion", PrimaryKeyName = "PK_Expansion",
                Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                    new() { Name = "Description", Type = "varchar", Length = "100", Nullable = false },
                    new() { Name = "Item", Type = "varchar", Length = "30", Nullable = false }],
                Indexes = [new() { Name = "IX_Expansion_Item", Columns = "Item" }]
            };
            project = store.SaveTable(admin, project.Id, project.Revision, table);
            var initial = await tools.CompareAsync(admin, project.Id, profile.Id);
            await tools.ExecuteAsync(admin, project.Id, initial.Id, database);
            using (var insert = target.CreateCommand())
            {
                insert.CommandText = "INSERT dbo.Expansion VALUES(1,'中文说明ABC','123456789012345678901234567890')";
                await insert.ExecuteNonQueryAsync();
            }
            table.Columns[1].Type = "nvarchar";
            table.Columns[2].Type = "nvarchar";
            table.Columns[2].Length = "50";
            project = store.SaveTable(admin, project.Id, project.Revision, table);
            var plan = await tools.CompareAsync(admin, project.Id, profile.Id);
            check("SQL whole project auto approves varchar to nvarchar with existing rows", !plan.AllowDataLoss && plan.Warnings.Any(w => w.Code == "SafeTypeExpansion"));
            await tools.ExecuteAsync(admin, project.Id, plan.Id, database);
            using (var read = target.CreateCommand())
            {
                read.CommandText = "SELECT COUNT(*) FROM dbo.Expansion WHERE Description=N'中文说明ABC' AND Item=N'123456789012345678901234567890'";
                check("SQL automatic widening preserves Chinese and full data", Convert.ToInt32(await read.ExecuteScalarAsync()) == 1);
                read.CommandText = "SELECT collation_name FROM sys.databases WHERE name=DB_NAME()";
                check("SQL automatic widening preserves database collation", (string?)await read.ExecuteScalarAsync() == "Chinese_PRC_CI_AS");
            }
            check("SQL widening round trip has zero diff", (await tools.CompareAsync(admin, project.Id, profile.Id)).Changes.Count == 0);

            table.Columns[1].Length = "200";
            project = store.SaveTable(admin, project.Id, project.Revision, table);
            var single = await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id);
            check("SQL single table widening auto approved", single.Warnings.Any(w => w.Code == "SafeTypeExpansion"));
            await tools.ExecuteAsync(admin, project.Id, single.Id, database);
            table.Columns[1].Length = "250";
            table.Columns[2].Length = "10";
            project = store.SaveTable(admin, project.Id, project.Revision, table);
            var mixed = await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id);
            check("SQL mixed shrinking plan never auto approved", !mixed.Warnings.Any(w => w.Code == "SafeTypeExpansion"));
            check("SQL shrinking warning identifies exact column and new capacity", mixed.Warnings.Any(w => w.Code == "TypeCapacityReduction" && w.Issues.Any(i => i.Message.Contains("Item") && i.Message.Contains("10"))));
            var blocked = false;
            try { await tools.ExecuteAsync(admin, project.Id, mixed.Id, database); }
            catch (DacServicesException) { blocked = true; }
            check("SQL mixed shrinking with data blocked", blocked);
            using (var verify = target.CreateCommand())
            {
                verify.CommandText = "SELECT COUNT(*) FROM dbo.Expansion WHERE LEN(Item)=30";
                check("SQL blocked shortening preserves existing data", Convert.ToInt32(await verify.ExecuteScalarAsync()) == 1);
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            using var drop = master.CreateCommand();
            drop.CommandText = $"ALTER DATABASE {SqlServerDdl.Q(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {SqlServerDdl.Q(database)};";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
