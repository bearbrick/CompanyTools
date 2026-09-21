using System.Security.Claims;
using DbStudio.Core;
using Microsoft.Data.SqlClient;

/// <summary>临时库回归：空列删除、执行前数据变化、事务回滚及混合计划保护。</summary>
internal static class EmptyColumnDeletionChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var target = new DesignProject { Tables = [new() { Name = "Sample", Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }, new() { Name = "Accidental", Label = "误加字段" }] }] };
        var source = ModelJson.Clone(target);
        source.Tables[0].Columns.RemoveAt(1);
        var targetBytes = SqlServerTools.BuildPackage(target);
        List<EmptyColumnDeletion.Column> Assess(bool prune = true) => EmptyColumnDeletion.Assess(SqlServerTools.BuildPackage(source), targetBytes, "SampleDb", prune, default);
        check("Empty deletion recognizes only removed column including its description", Assess().Single().Name == "Accidental");
        check("Empty deletion assesses real column removal even when prune is off", Assess(false).Single().Name == "Accidental");
        source.Tables[0].Columns[0].Type = "bigint";
        check("Empty deletion refuses mixed type expansion", Assess().Count == 0);
        source.Tables[0].Columns[0].Type = "int";
        source.Tables[0].Label = "改说明";
        check("Empty deletion refuses unrelated description changes", Assess().Count == 0);
        source.Tables[0].Label = "";
        source.Tables.Add(new() { Name = "NewTable", Columns = [new() { Name = "Id", Type = "int" }] });
        check("Empty deletion refuses mixed create table", Assess().Count == 0);
        source.Tables.RemoveAt(1);
        target.Tables[0].Columns[1].Default = "N''";
        targetBytes = SqlServerTools.BuildPackage(target);
        check("Empty deletion does not implicitly remove default constraints", Assess().Count == 0);
    }

    internal static async Task RunAsync(StudioStore store, ClaimsPrincipal admin, string server, Action<string, bool> check)
    {
        var database = "DbStudioEmptyColumn_" + Guid.NewGuid().ToString("N")[..12];
        var builder = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true };
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync();
        using (var cmd = master.CreateCommand()) { cmd.CommandText = "CREATE DATABASE " + SqlServerDdl.Q(database); await cmd.ExecuteNonQueryAsync(); }
        try
        {
            builder.InitialCatalog = database;
            await using var target = new SqlConnection(builder.ConnectionString);
            await target.OpenAsync();
            async Task Exec(string sql) { using var cmd = target.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
            async Task<int> Scalar(string sql) { using var cmd = target.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(await cmd.ExecuteScalarAsync()); }
            var project = store.NewProject(admin, "空列删除回归", database);
            var profile = store.SaveConnection(admin, new DatabaseConnection { ProjectId = project.Id, Server = server, Database = database, Name = "隔离空列测试", TrustServerCertificate = true }, "");
            var tools = new SqlServerTools(store);
            var table = new TableDesign { Name = "EmptyColumns", Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }, new() { Name = "Column15", Label = "误加" }, new() { Name = "AnotherEmpty" }] };
            project = store.SaveLabeledTable(admin, project.Id, project.Revision, table);
            var create = await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id);
            await tools.ExecuteAsync(admin, project.Id, create.Id, database);
            await Exec("INSERT dbo.EmptyColumns(Id) VALUES (1), (2)");
            table.Columns.RemoveRange(1, 2);
            project = store.SaveLabeledTable(admin, project.Id, project.Revision, table);
            var plan = await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id, prune: false);
            check("SQL nonempty table with NULL-only columns auto approved", plan.Warnings.Any(w => w.Code == "EmptyColumnDeletion") && !plan.AllowDataLoss);
            check("SQL actual column removal is approved with prune unchecked", !plan.Prune && plan.Script.Contains("DROP COLUMN [Column15]"));
            check("SQL empty deletion preview shows transactional value guard", plan.Script.Contains("TABLOCKX, HOLDLOCK") && plan.Script.Contains("IS NOT NULL") && plan.Script.Contains("DROP COLUMN [Column15]"));
            await Exec("UPDATE dbo.EmptyColumns SET Column15=N'后来写入' WHERE Id=1");
            bool blocked = false;
            try { await tools.ExecuteAsync(admin, project.Id, plan.Id, database); }
            catch (SqlException ex) when (ex.Number == 51000) { blocked = true; }
            check("SQL data written after preview blocks deletion", blocked);
            check("SQL failed multi-column delete preserves both columns and values", await Scalar("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.EmptyColumns') AND name IN ('Column15','AnotherEmpty')") == 2
                && await Scalar("SELECT COUNT(*) FROM dbo.EmptyColumns WHERE Column15=N'后来写入'") == 1);
            var nonempty = await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id, prune: true);
            check("SQL populated column is not automatically approved", !nonempty.Warnings.Any(w => w.Code == "EmptyColumnDeletion"));
            check("SQL populated column explains why deletion is blocked", nonempty.Warnings.Any(w => w.Code == "EmptyColumnDeletionBlocked" && w.Issues.Any(i => i.Message.Contains("Column15") && i.Message.Contains("非 NULL"))));
            await Exec("UPDATE dbo.EmptyColumns SET Column15=N''");
            check("SQL empty string is data and is not auto approved", !(await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id, prune: true)).Warnings.Any(w => w.Code == "EmptyColumnDeletion"));
            await Exec("UPDATE dbo.EmptyColumns SET Column15=NULL");
            var retry = await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id, prune: false);
            await tools.ExecuteAsync(admin, project.Id, retry.Id, database);
            check("SQL NULL-only columns deleted and business rows preserved", await Scalar("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.EmptyColumns')") == 1
                && await Scalar("SELECT COUNT(*) FROM dbo.EmptyColumns") == 2);
            check("SQL empty column deletion round trip has zero diff", (await tools.CompareTableAsync(admin, project.Id, profile.Id, table.Id, prune: true)).Changes.Count == 0);
            await Exec("ALTER TABLE dbo.EmptyColumns ADD A nvarchar(20) NULL, B nvarchar(20) NULL; CREATE INDEX IX_B ON dbo.EmptyColumns(B)");
            var columns = new List<EmptyColumnDeletion.Column> { new("dbo", "EmptyColumns", "A"), new("dbo", "EmptyColumns", "B") };
            bool rolledBack = false;
            try { EmptyColumnDeletion.Execute(builder.ConnectionString, columns, default); }
            catch (SqlException) { rolledBack = true; }
            check("SQL DDL failure after first drop rolls back all deleted columns", rolledBack && await Scalar("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.EmptyColumns') AND name IN ('A','B')") == 2);
            await Exec("CREATE FUNCTION dbo.HideRows(@Id int) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS Visible WHERE @Id < 0");
            await Exec("CREATE SECURITY POLICY dbo.TestPolicy ADD FILTER PREDICATE dbo.HideRows(Id) ON dbo.EmptyColumns WITH (STATE=ON)");
            check("SQL row security cannot disguise data as empty", !EmptyColumnDeletion.AreEmpty(builder.ConnectionString, columns, default));
            bool securityBlocked = false;
            try { EmptyColumnDeletion.Execute(builder.ConnectionString, columns, default); }
            catch (SqlException ex) when (ex.Number == 51000) { securityBlocked = true; }
            check("SQL execution refuses row security filtered tables", securityBlocked);
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
