using System.Security.Claims;
using System.Text.Json;
using DbStudio.Core;

/// <summary>核验批量移动的边界、稳定顺序、字段完整性和保存后的读取结果。</summary>
internal static class ColumnOrderingChecks
{
    internal static void Run(StudioStore store, ClaimsPrincipal admin, Action<string, bool> check)
    {
        void Expect(string selectedNames, int direction, string expected)
        {
            var columns = "ABCDE".Select(name => new ColumnDesign { Id = name.ToString(), Name = name.ToString() }).ToList();
            var selected = selectedNames.Select(name => name.ToString()).ToHashSet();
            var changed = ColumnOrdering.Move(columns, selected, direction);
            check($"Batch columns {selectedNames} direction {direction} => {expected}",
                string.Concat(columns.Select(c => c.Name)) == expected && changed == (expected != "ABCDE")
                && selected.SetEquals(selectedNames.Select(name => name.ToString())));
        }
        Expect("BCD", -1, "BCDAE");
        Expect("BCD", 1, "AEBCD");
        Expect("BD", -1, "BADCE");
        Expect("BD", 1, "ACBED");
        Expect("ABD", -1, "ABDCE");
        Expect("BDE", 1, "ACBDE");
        Expect("ABCDE", -1, "ABCDE");
        Expect("ABCDE", 1, "ABCDE");
        Expect("", -1, "ABCDE");

        var table = new TableDesign
        {
            Name = "ColumnOrder",
            Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                new() { Name = "Amount", Type = "decimal", Precision = 12, Scale = 2, Default = "0" },
                new() { Name = "Name", Type = "nvarchar", Label = "名称", Length = "100", Comment = "业务说明" }]
        };
        var before = table.Columns.ToDictionary(c => c.Id, c => JsonSerializer.Serialize(c, ModelJson.Options));
        var selection = table.Columns.Skip(1).Select(c => c.Id).ToHashSet();
        check("Batch move boundary availability matches selected rows", ColumnOrdering.CanMove(table.Columns, selection, -1) && !ColumnOrdering.CanMove(table.Columns, selection, 1));
        ColumnOrdering.Move(table.Columns, selection, -1);
        check("Batch movement retains every field attribute and identity", table.Columns.All(c => before[c.Id] == JsonSerializer.Serialize(c, ModelJson.Options)));
        var project = store.NewProject(admin, "字段批量移动验证", "隔离测试");
        project = store.SaveTable(admin, project.Id, project.Revision, table);
        var saved = store.Projects(admin).Single(p => p.Id == project.Id).Tables.Single();
        check("Batch moved order survives save and reload", saved.Columns.Select(c => c.Name).SequenceEqual(["Amount", "Name", "Id"]));
        var staleSelection = new HashSet<string> { "no-such-field" };
        check("Stale selection and invalid direction cannot move fields", !ColumnOrdering.Move(table.Columns, staleSelection, -1)
            && !ColumnOrdering.Move(table.Columns, selection, 0));
    }
}
