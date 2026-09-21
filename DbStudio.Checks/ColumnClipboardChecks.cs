using DbStudio.Core;

/// <summary>验证字段格式兼容、快照隔离、重名避让以及重复粘贴不会共享对象。</summary>
internal static class ColumnClipboardChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var first = new ColumnDesign { Name = "Amount", Label = "金额", Type = "decimal", Precision = 16, Scale = 4,
            Nullable = false, Default = "0", DefaultConstraintName = "DF_Source_Amount", DefaultConstraintSystemNamed = true,
            PrimaryKeyOrder = 1, Identity = true, Comment = "业务备注", InputLimit = "12" };
        var second = new ColumnDesign { Name = "Amount_copy", Type = "nvarchar", Length = "100", Collation = "Chinese_PRC_CI_AS" };
        var clipboard = new ColumnClipboard();
        clipboard.Capture([first, second], new HashSet<string> { second.Id, first.Id });
        var text = clipboard.Serialize();
        first.Label = "来源后续编辑";
        var parsed = ColumnClipboard.Parse(text);
        check("Clipboard accepts valid sparse field definitions", ColumnClipboard.Parse("{\"Format\":\"DbStudio.Columns\",\"Version\":1,\"Columns\":[{\"Name\":\"TaskId\",\"Type\":\"int\",\"Label\":\"任务ID\",\"Nullable\":false}]}").Count == 1);
        var target = new List<ColumnDesign> { new() { Name = "amount", Type = "int" } };
        var copies = parsed.CreateCopies(target);
        check("Clipboard preserves source order and reserves copied names case insensitively", copies.Select(c => c.Name).SequenceEqual(["Amount_copy2", "Amount_copy"]));
        check("Clipboard preserves complete field metadata and frozen snapshot", copies[0].Label == "金额" && copies[0].Comment == "业务备注"
            && copies[0].Precision == 16 && copies[0].Scale == 4 && !copies[0].Nullable && copies[0].Default == "0" && copies[0].InputLimit == "12"
            && copies[1].Length == "100" && copies[1].Collation == second.Collation && text.Contains("金额"));
        check("Clipboard clears target-conflicting constraint identities and retains target untouched", copies[0].PrimaryKeyOrder == 0 && !copies[0].Identity
            && copies[0].DefaultConstraintName == "" && !copies[0].DefaultConstraintSystemNamed && target.Count == 1 && first.Identity);
        var again = parsed.CreateCopies(target);
        copies[0].Comment = "目标修改";
        check("Repeated clipboard paste creates independent objects and IDs", again[0].Comment == "业务备注" && again.Select(c => c.Id).Intersect(copies.Select(c => c.Id)).Count() == 0);
        var unique = parsed.CreateCopies([]);
        check("Paste into other table keeps available original names", unique[0].Name == "Amount" && unique[1].Name == "Amount_copy");
        var longColumn = new ColumnDesign { Name = new string('X', 128) };
        clipboard.Capture([longColumn], new HashSet<string> { longColumn.Id });
        check("Clipboard collision suffix respects identifier limit", clipboard.CreateCopies([longColumn]).Single().Name.Length == 128);
        bool Rejected(string value) { try { ColumnClipboard.Parse(value); return false; } catch (InvalidOperationException) { return true; } }
        check("Clipboard rejects Excel text unrelated JSON and unsupported versions", Rejected("Id\t编号\tint") && Rejected("{}") && Rejected(text.Replace("\"Version\": 1", "\"Version\": 2")));
        check("Clipboard rejects malformed columns and oversized payloads", Rejected("{\"Format\":\"DbStudio.Columns\",\"Version\":1,\"Columns\":[{\"Name\":\"Bad\"}]}")
            && Rejected(text.Replace("\"Comment\": \"业务备注\"", "\"Comment\": null")) && Rejected(new string('x', ColumnClipboard.MaxCharacters + 1)));
    }
}
