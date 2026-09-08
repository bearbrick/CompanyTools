using System.Security.Claims;
using System.Text.Json;
using DbStudio.Core;

/// <summary>验证复制后的结构保真、命名空间隔离及持久化边界，使用隔离测试库。</summary>
internal static class TableCopyChecks
{
    internal static void Run(StudioStore store, ClaimsPrincipal admin, ClaimsPrincipal outsider, Action<string, bool> check)
    {
        var project = store.NewProject(admin, "复制表检查", "独立测试项目");
        var source = new TableDesign
        {
            Name = "Source", Label = "来源表", Module = "原模块", Comment = "表备注",
            PrimaryKeyName = "PK_Target", PrimaryKeyClustered = false,
            Columns =
            [
                new() { Name = "Id", Type = "bigint", Label = "主键", Nullable = false, PrimaryKeyOrder = 1, Identity = true, IdentitySeed = 10, IdentityIncrement = 2 },
                new() { Name = "ParentId", Type = "bigint", Label = "上级" },
                new() { Name = "Code", Type = "varchar", Length = "80", Label = "编码", Default = "('')", DefaultConstraintName = "DF_Source_Code", InputLimit = "40", Comment = "业务输入规则", Collation = "Chinese_PRC_CI_AS" },
                new() { Name = "Amount", Type = "decimal", Precision = 16, Scale = 4, Default = "((0))" },
                new() { Name = "CreatedAt", Type = "datetime2", TemporalScale = 3 },
                new() { Name = "Ratio", Type = "float", FloatPrecision = 24 },
                new() { Name = "Total", Type = "decimal", Computed = "[Amount] * 2", Persisted = true }
            ],
            Indexes = [new() { Name = "UQ_Target_1", Columns = "Code", Unique = true, IsConstraint = true }],
            Checks = [new() { Name = "CK_Target_1", Expression = "[Amount] >= 0" }]
        };
        source.ForeignKeys.Add(new() { Name = "FK_Target_1", Columns = "ParentId", TargetColumns = "Id", TargetTableId = source.Id });
        project = store.SaveTable(admin, project.Id, project.Revision, source);
        var original = JsonSerializer.Serialize(source, ModelJson.Options);
        var revision = project.Revision;
        var options = new TableCopyOptions { Name = " Target ", Schema = " dbo ", Label = "新表定义", Module = "新模块", Comment = "新表备注" };
        var copy = DesignEditing.CopyTable(project, source, options);

        check("Copy uses final table information", copy.Name == "Target" && copy.Schema == "dbo" && copy.Label == options.Label && copy.Module == options.Module && copy.Comment == options.Comment);
        check("Preparing or cancelling a copy leaves source and storage untouched", original == JsonSerializer.Serialize(source, ModelJson.Options)
            && store.Projects(admin).Single(p => p.Id == project.Id).Revision == revision);
        var comparableColumns = ModelJson.Clone(copy.Columns);
        for (var i = 0; i < source.Columns.Count; i++)
        {
            comparableColumns[i].Id = source.Columns[i].Id;
            comparableColumns[i].DefaultConstraintName = source.Columns[i].DefaultConstraintName;
            comparableColumns[i].DefaultConstraintSystemNamed = source.Columns[i].DefaultConstraintSystemNamed;
        }
        check("Copy preserves every field property and order except independent identities", JsonSerializer.Serialize(comparableColumns, ModelJson.Options) == JsonSerializer.Serialize(source.Columns, ModelJson.Options));
        check("Copy allocates non-conflicting schema constraint names", copy.PrimaryKeyName != source.PrimaryKeyName
            && copy.Indexes[0].Name != source.Indexes[0].Name && copy.ForeignKeys[0].Name != source.ForeignKeys[0].Name && copy.Checks[0].Name != source.Checks[0].Name);
        check("Copied self-reference targets new table", copy.ForeignKeys[0].TargetTableId == copy.Id);
        project = store.SaveTable(admin, project.Id, project.Revision, copy);
        check("Copy creates exactly one table and revision without changing original", project.Tables.Count == 2 && project.Revision == revision + 1
            && original == JsonSerializer.Serialize(project.Tables.Single(t => t.Id == source.Id), ModelJson.Options));
        check("Original and copied constraints build together in DacFx", SqlServerTools.BuildPackage(project).Length > 0);
        check("Duplicate table names rejected ignoring case and whitespace", Rejected<InvalidOperationException>(() => DesignEditing.CopyTable(project, source, new() { Name = " target ", Schema = "DBO" })));
        check("Blank table name rejected", Rejected<InvalidOperationException>(() => DesignEditing.CopyTable(project, source, new() { Name = " " })));
        var longCopy = DesignEditing.CopyTable(project, source, new() { Name = new string('T', 128), Schema = "archive" });
        var longProject = ModelJson.Clone(project);
        longProject.Tables.Add(longCopy);
        check("Long new table names produce valid constraint identifiers", SqlServerDdl.Validate(longProject, longCopy).Count == 0 && SqlServerTools.BuildPackage(longProject).Length > 0);
        var nextCopy = DesignEditing.CopyTable(project, source, new() { Name = "Another" });
        check("Copy save rejects stale revision", Rejected<InvalidOperationException>(() => store.SaveTable(admin, project.Id, revision, nextCopy)));
        check("Copy save enforces project permissions", Rejected<UnauthorizedAccessException>(() => store.SaveTable(outsider, project.Id, project.Revision, nextCopy)));
        check("Rejected copy attempts do not create tables or revisions", store.Projects(admin).Single(p => p.Id == project.Id).Revision == project.Revision);
        copy.Columns[2].Comment = "副本后续编辑";
        check("Editing copied fields cannot mutate original", source.Columns[2].Comment == "业务输入规则");
    }

    /// <summary>检查指定业务拒绝，其他异常继续抛出，避免掩盖真正的实现错误。</summary>
    private static bool Rejected<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }
}
