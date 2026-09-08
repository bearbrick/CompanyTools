using DbStudio.Core;

/// <summary>反推选择和预览必须与真实合并一致；不连接任何实际数据库。</summary>
internal static class ReversePreviewChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var existing = new TableDesign { Name = "Existing", Label = "业务定义", Comment = "设计备注", Module = "业务模块", Columns = [new() { Name = "Id", Type = "int", Default = "0", InputLimit = "10" }, new() { Name = "At", Type = "datetime2" }] };
        var localOnly = new TableDesign { Name = "DesignOnly", Columns = [new() { Name = "Id", Type = "int" }] };
        var project = new DesignProject { Tables = [existing, localOnly] };
        var actual = ModelJson.Clone(existing);
        actual.Id = "db-1";
        actual.Label = "数据库定义";
        actual.Comment = "";
        actual.Columns[0].Id = "db-column-1";
        actual.Columns[0].Length = "4";
        actual.Columns[0].Precision = 10;
        actual.Columns[0].Scale = 0;
        actual.Columns[0].Default = "((0))";
        actual.Columns[1].TemporalScale = 7;
        var newTable = new TableDesign { Id = "db-2", Name = "DatabaseOnly", Columns = [new() { Name = "Id", Type = "int" }] };
        var snapshot = new DatabaseSnapshot(new() { Tables = [actual, newTable] }, []);
        var preview = DatabaseMerge.Preview(project, snapshot);
        check("Reverse ignores source IDs unused type metadata and default parentheses", preview.UnchangedCount == 1 && preview.NewCount == 1 && preview.ChangedCount == 0);
        check("Reverse reports design-only tables without deleting them", preview.DesignOnlyCount == 1);
        var empty = DatabaseMerge.Apply(project, preview, []);
        check("Empty selection changes nothing", System.Text.Json.JsonSerializer.Serialize(empty, ModelJson.Options) == System.Text.Json.JsonSerializer.Serialize(project, ModelJson.Options));
        var added = DatabaseMerge.Apply(project, preview, ["db-2"]);
        check("Selection adds only requested database-only table", added.Tables.Count == 3 && added.Tables.Any(t => t.Name == "DesignOnly"));

        actual.Columns[0].Default = "1";
        actual.Columns.Add(new() { Name = "Code", Type = "nvarchar", Length = "30" });
        actual.Indexes.Add(new() { Name = "IX_Code", Columns = "Code" });
        preview = DatabaseMerge.Preview(project, snapshot);
        var row = preview.Tables.Single(t => t.SourceId == "db-1");
        check("Reverse details identify exact default column and index changes", row.Differences.Any(d => d.Object == "Id" && d.Property == "默认值" && d.Before == "0" && d.After == "1") && row.Differences.Any(d => d.Object == "Code" && d.Property == "新增字段") && row.Differences.Any(d => d.Object == "IX_Code" && d.Property == "新增索引"));
        var merged = DatabaseMerge.Apply(project, preview, ["db-1"]);
        var table = merged.Tables.Single(t => t.Id == existing.Id);
        check("Selective merge preserves IDs and business metadata", merged.Tables.Count == 2 && table.Label == existing.Label && table.Comment == existing.Comment && table.Module == existing.Module && table.Columns[0].Id == existing.Columns[0].Id && table.Columns[0].InputLimit == "10");
        check("Merged tables become unchanged on next preview", DatabaseMerge.Preview(merged, snapshot).ChangedCount == 0);
        var invalidRejected = false;
        try { DatabaseMerge.Apply(project, preview, ["not-in-snapshot"]); } catch (InvalidOperationException) { invalidRejected = true; }
        check("Unknown reverse selection rejected", invalidRejected);
        actual.Columns.RemoveAll(c => c.Name == "At");
        check("Removed database columns explicitly identified", DatabaseMerge.Preview(project, snapshot).Tables[0].Differences.Any(d => d.Object == "At" && d.Property == "移除字段"));
        check("Preview does not mutate original design", project.Tables[0].Columns.Count == 2 && project.Tables[0].Columns[0].Default == "0");
    }
}
