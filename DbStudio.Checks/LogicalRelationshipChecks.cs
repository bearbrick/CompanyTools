using System.Text.Json;
using DbStudio.Core;

/// <summary>逻辑关系必须完整保存在设计链路中，同时与数据库约束生成和反推严格隔离。</summary>
internal static class LogicalRelationshipChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var parent = new TableDesign
        {
            Id = "parent",
            Name = "Parent",
            Columns = [new() { Name = "Code", Type = "nvarchar", Length = "40", Nullable = false }]
        };
        var child = new TableDesign
        {
            Id = "child",
            Name = "Child",
            Columns = [new() { Name = "ParentCode", Type = "nvarchar", Length = "40" }],
            ForeignKeys = [new()
            {
                Name = "FK_Child_Parent_Logical",
                IsLogical = true,
                Columns = "ParentCode",
                TargetTableId = parent.Id,
                TargetColumns = "Code",
                OnDelete = "not-used",
                OnUpdate = "not-used"
            }]
        };
        var project = new DesignProject { Tables = [parent, child] };

        check("Logical relationship allows a non-unique design target and ignores physical actions",
            SqlServerDdl.Validate(project, child).Count == 0);
        var singleSql = SqlServerDdl.Generate(project, child);
        var projectSql = SqlServerDdl.GenerateProject(project);
        check("Logical relationship never generates a database constraint",
            !singleSql.Contains("FK_Child_Parent_Logical") && !projectSql.Contains("FK_Child_Parent_Logical")
            && !singleSql.Contains("FOREIGN KEY") && !projectSql.Contains("FOREIGN KEY"));

        var physical = ModelJson.Clone(child);
        physical.ForeignKeys[0].IsLogical = false;
        check("Physical foreign key retains unique-target and action validation",
            SqlServerDdl.Validate(project, physical).Count > 0);

        var oldJson = """{"Name":"FK_Old","Columns":"ParentId","TargetTableId":"parent","TargetColumns":"Id"}""";
        check("Historical relationship JSON remains a physical foreign key",
            !JsonSerializer.Deserialize<ForeignKeyDesign>(oldJson, ModelJson.Options)!.IsLogical);

        var actualParent = ModelJson.Clone(parent);
        actualParent.Id = "db-parent";
        var actualChild = ModelJson.Clone(child);
        actualChild.Id = "db-child";
        actualChild.ForeignKeys.Clear();
        var snapshot = new DatabaseSnapshot(new()
        {
            Tables = [actualParent, actualChild]
        }, []);
        var preview = DatabaseMerge.Preview(project, snapshot);
        check("Database reverse preserves design-only logical relationships",
            preview.ChangedCount == 0 && preview.Tables.Single(row => row.Table.Name == "Child").Table.ForeignKeys.Single().IsLogical);

        actualChild.ForeignKeys.Add(new()
        {
            Name = "FK_Child_Parent_Logical",
            Columns = "ParentCode",
            TargetTableId = actualParent.Id,
            TargetColumns = "Code"
        });
        preview = DatabaseMerge.Preview(project, snapshot);
        var converted = preview.Tables.Single(row => row.Table.Name == "Child");
        check("Database reverse reports a matching physical constraint as a relationship type change",
            converted.Status == "有变化" && converted.Table.ForeignKeys.Count == 1 && !converted.Table.ForeignKeys[0].IsLogical);

        var copied = DesignEditing.CopyTable(project, child);
        check("Table copy preserves logical relationship semantics and remaps self references safely",
            copied.ForeignKeys.Single().IsLogical);
        check("Archive distinguishes logical and physical relationship markers",
            StructureArchiveText.ForeignKeyMarker(project, child, child.Columns[0]) == "○");
        var notes = string.Join('\n', StructureArchiveText.Notes(project, child));
        check("Archive describes logical relationships without referential actions",
            notes.Contains("逻辑关系") && notes.Contains("不生成数据库约束") && !notes.Contains("删除："));
    }
}
