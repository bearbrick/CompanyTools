using DbStudio.Core;
using Microsoft.SqlServer.TransactSql.ScriptDom;

/// <summary>验证类型容量与整份计划隔离，避免一列扩容让其他风险操作被一起放行。</summary>
internal static class TypeExpansionChecks
{
    internal static void Run(Action<string, bool> check)
    {
        bool Expands(string before, string after) => SchemaTypeExpansion.IsExpansion(Type(before), Type(after));
        check("varchar to equal nvarchar is expansion", Expands("varchar(100)", "nvarchar(100)"));
        check("varchar to larger nvarchar is expansion", Expands("varchar(30)", "nvarchar(50)"));
        check("variable length and integer expansions allowed", Expands("nvarchar(30)", "nvarchar(max)") && Expands("int", "bigint"));
        check("shortening and unicode loss stay protected", !Expands("nvarchar(100)", "nvarchar(50)") && !Expands("nvarchar(100)", "varchar(200)"));
        check("max unicode conversion cannot prove capacity", !Expands("varchar(max)", "nvarchar(max)"));
        check("decimal requires both fractional and integral capacity", Expands("decimal(12,2)", "decimal(16,4)") && !Expands("decimal(12,2)", "decimal(12,4)") && !Expands("decimal(12,4)", "decimal(16,2)"));
        check("capacity reduction identifies shorter Unicode length and decimal scale", SchemaTypeExpansion.IsReduction(Type("varchar(100)"), Type("nvarchar(50)")) && SchemaTypeExpansion.IsReduction(Type("decimal(12,4)"), Type("decimal(16,2)")));

        var table = new TableDesign
        {
            Name = "Expansion", PrimaryKeyName = "PK_Expansion",
            Columns = [new() { Name = "Id", Type = "int", PrimaryKeyOrder = 1, Nullable = false },
                new() { Name = "Text", Type = "varchar", Length = "100", Nullable = false, Default = "''", DefaultConstraintName = "DF_Expansion_Text" },
                new() { Name = "Other", Type = "varchar", Length = "100" }],
            Indexes = [new() { Name = "IX_Expansion_Text", Columns = "Text" }],
            Checks = [new() { Name = "CK_Expansion_Id", Expression = "Id > 0" }]
        };
        var project = new DesignProject { Tables = [table] };
        var baseline = SqlServerTools.BuildPackage(project);
        table.Columns[1].Type = "nvarchar";
        bool Approved(bool prune = false) => SchemaTypeExpansion.Assess(SqlServerTools.BuildPackage(project), baseline, "ExpansionChecks", prune).Count > 0;
        check("pure expansion with existing index default and check auto approved", Approved());
        table.Columns[1].Label = "新定义名";
        check("expansion plus description auto approved", Approved());
        table.Columns[2].Length = "50";
        check("mixed expansion and shortening stays protected", !Approved());
        table.Columns[2].Length = "100";
        table.Columns[1].Default = "'changed'";
        check("mixed expansion and default changes stay protected", !Approved());
        table.Columns[1].Default = "''";
        table.Columns[2].Nullable = false;
        check("mixed expansion and NOT NULL stays protected", !Approved());
        table.Columns.RemoveAt(2);
        check("mixed expansion and column deletion stays protected", !Approved(true));
    }

    private static DataTypeReference Type(string sql)
    {
        var script = (TSqlScript)new TSql160Parser(true).Parse(new StringReader($"CREATE TABLE dbo.T(C {sql});"), out var errors);
        if (errors.Count != 0) { throw new InvalidOperationException("Invalid test type: " + sql); }
        return ((CreateTableStatement)script.Batches[0].Statements[0]).Definition.ColumnDefinitions[0].DataType;
    }
}
