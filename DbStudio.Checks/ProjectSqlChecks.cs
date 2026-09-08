using DbStudio.Core;
using Microsoft.SqlServer.TransactSql.ScriptDom;

/// <summary>项目 SQL 的跨表顺序、循环引用、完整内容和非法设计校验。</summary>
internal static class ProjectSqlChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var a = new TableDesign { Schema = "business", Name = "First", Label = "甲表", Columns = [
            new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
            new() { Name = "OtherId", Type = "int" },
            new() { Name = "Amount", Type = "decimal", Precision = 16, Scale = 4, Default = "0", Label = "金额" }],
            Indexes = [new() { Name = "IX_First_OtherId", Columns = "OtherId" }],
            Checks = [new() { Name = "CK_First_Amount", Expression = "[Amount] >= 0" }] };
        var b = new TableDesign { Schema = "business", Name = "Second", Columns = [
            new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }, new() { Name = "OtherId", Type = "int" }] };
        a.ForeignKeys.Add(new() { Name = "FK_First_Second", Columns = "OtherId", TargetTableId = b.Id, TargetColumns = "Id" });
        b.ForeignKeys.Add(new() { Name = "FK_Second_First", Columns = "OtherId", TargetTableId = a.Id, TargetColumns = "Id" });
        b.ForeignKeys.Add(new() { Name = "FK_Second_Self", Columns = "OtherId", TargetTableId = b.Id, TargetColumns = "Id" });
        var project = new DesignProject { Name = "项目 SQL 检查", Tables = [a, b] };
        var sql = SqlServerDdl.GenerateProject(project);
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        check("Project SQL parses with cross-table and self references", errors.Count == 0);
        check("Project SQL creates every table before any foreign key", sql.LastIndexOf("CREATE TABLE", StringComparison.Ordinal) < sql.IndexOf("FOREIGN KEY", StringComparison.Ordinal));
        check("Project SQL declares each schema only once", sql.Split("IF SCHEMA_ID").Length == 2);
        check("Project SQL retains fields indexes defaults checks and Chinese descriptions", sql.Contains("decimal(16,4)")
            && sql.Contains("IX_First_OtherId") && sql.Contains("DEFAULT (0)") && sql.Contains("CK_First_Amount") && sql.Contains("金额"));
        check("Project SQL includes every foreign key once", sql.Split("FOREIGN KEY").Length == 4);
        var single = SqlServerDdl.Generate(project, a);
        check("Single-table SQL still includes its own foreign keys", single.Contains("FK_First_Second") && !single.Contains("CREATE TABLE [business].[Second]"));
        a.Columns[0].Type = "unsupported";
        check("Project SQL rejects invalid tables instead of returning partial output", Reject(() => SqlServerDdl.GenerateProject(project)));
        check("Project SQL rejects an empty project", Reject(() => SqlServerDdl.GenerateProject(new())));
    }

    private static bool Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }
}
