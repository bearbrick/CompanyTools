using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbStudio.Core;

/// <summary>反推预览中的单项结构变化；左右值分别来自当前设计和数据库。</summary>
public record ReverseDifference(string Object, string Property, string Before, string After);

/// <summary>一张读取到的表及其实际合并结果。SourceId 用于选择，不依赖设计内部 ID。</summary>
public record ReverseTableChange(string SourceId, TableDesign Table, string Status, List<ReverseDifference> Differences);

/// <summary>只读反推预览；未落库的设计表保留，不作为待导入项。</summary>
public record ReversePreview(List<ReverseTableChange> Tables, int DesignOnlyCount)
{
    public int ChangedCount => Tables.Count(t => t.Status == "有变化");
    public int NewCount => Tables.Count(t => t.Status == "数据库独有");
    public int UnchangedCount => Tables.Count(t => t.Status == "无变化");
}

/// <summary>
/// 反推预览与提交共用同一套合并规则。保留业务元数据，按有效 DDL 属性比较结构，
/// 排除随机 ID、无关类型参数、约束枚举顺序和默认值外围括号造成的假变化。
/// 本类只操作独立内存快照，不连接或修改任何数据库。
/// </summary>
public static class DatabaseMerge
{
    /// <summary>对每张数据库表建立合并候选，计算真实变化；不会改写原项目或反推快照。</summary>
    public static ReversePreview Preview(DesignProject project, DatabaseSnapshot snapshot)
    {
        var incoming = ModelJson.Clone(snapshot.Project);
        TableDesign? Existing(TableDesign t) => project.Tables.FirstOrDefault(p => p.Schema.Equals(t.Schema, StringComparison.OrdinalIgnoreCase) && p.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
        var ids = incoming.Tables.ToDictionary(t => t.Id, t => Existing(t)?.Id ?? Guid.NewGuid().ToString("N"));
        var result = new List<ReverseTableChange>();
        foreach (var table in incoming.Tables)
        {
            var sourceId = table.Id;
            var old = Existing(table);
            table.Id = ids[sourceId];
            if (old != null)
            {
                table.Module = old.Module;
                table.Label = old.Label;
                table.Comment = old.Comment;
                foreach (var c in table.Columns)
                {
                    var previous = old.Columns.FirstOrDefault(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                    if (previous == null) continue;
                    c.Id = previous.Id;
                    c.Label = previous.Label;
                    c.Comment = previous.Comment;
                    c.InputLimit = previous.InputLimit;
                }
            }
            foreach (var fk in table.ForeignKeys) fk.TargetTableId = ids[fk.TargetTableId];
            var differences = old == null ? new List<ReverseDifference>() : Differences(old, table);
            result.Add(new(sourceId, table, old == null ? "数据库独有" : differences.Count == 0 ? "无变化" : "有变化", differences));
        }
        return new(result, project.Tables.Count(p => !incoming.Tables.Any(t => t.Schema.Equals(p.Schema, StringComparison.OrdinalIgnoreCase) && t.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>只应用指定的真实变化；新表引用未选择的表时，由调用方的完整性校验拒绝。</summary>
    public static DesignProject Apply(DesignProject project, ReversePreview preview, IReadOnlyCollection<string>? selectedIds = null)
    {
        if (selectedIds != null && selectedIds.Any(id => !preview.Tables.Any(t => t.SourceId == id)))
            throw new InvalidOperationException("所选表不属于本次反推预览，请重新读取。");
        var merged = ModelJson.Clone(project);
        foreach (var item in preview.Tables.Where(t => t.Status != "无变化" && (selectedIds == null || selectedIds.Contains(t.SourceId))))
        {
            var table = ModelJson.Clone(item.Table);
            var index = merged.Tables.FindIndex(t => t.Id == table.Id);
            if (index < 0) merged.Tables.Add(table);
            else merged.Tables[index] = table;
        }
        return merged;
    }

    private static List<ReverseDifference> Differences(TableDesign old, TableDesign table)
    {
        var result = new List<ReverseDifference>();
        void Add(string obj, string property, string before, string after)
        {
            if (before != after) result.Add(new(obj, property, before, after));
        }
        foreach (var c in old.Columns)
            if (!table.Columns.Any(n => n.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                result.Add(new(c.Name, "移除字段", SqlServerDdl.DataType(c), "数据库中不存在"));
        foreach (var c in table.Columns)
        {
            var previous = old.Columns.FirstOrDefault(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
            if (previous == null) { result.Add(new(c.Name, "新增字段", "设计中不存在", SqlServerDdl.DataType(c))); continue; }
            var before = ColumnProperties(previous);
            foreach (var pair in ColumnProperties(c)) Add(c.Name, pair.Key, before[pair.Key], pair.Value);
        }
        // 字段顺序会改变编辑器中的排列，明确展示，不能悄悄重排。
        if (old.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(table.Columns.Select(c => c.Name)))
            Add("表", "字段顺序", string.Join(", ", old.Columns.Select(c => c.Name)), string.Join(", ", table.Columns.Select(c => c.Name)));
        Add("表", "主键", PrimaryKey(old), PrimaryKey(table));
        CompareObjects("索引", old.Indexes, table.Indexes, i => i.Name, i => JsonSerializer.Serialize(new { i.Name, Columns = Names(i.Columns), Descending = Names(i.DescendingColumns, true), Include = Names(i.Include, true), i.Unique, i.IsConstraint, i.Clustered, Filter = Tokens(i.Filter) }));
        CompareObjects("外键", old.ForeignKeys, table.ForeignKeys, f => f.Name, f => JsonSerializer.Serialize(new { f.Name, Columns = Names(f.Columns), f.TargetTableId, TargetColumns = Names(f.TargetColumns), f.OnDelete, f.OnUpdate }));
        CompareObjects("检查约束", old.Checks, table.Checks, c => c.Name, c => Tokens(c.Expression));
        return result;

        void CompareObjects<T>(string kind, List<T> before, List<T> after, Func<T, string> name, Func<T, string> value)
        {
            foreach (var item in before)
                if (!after.Any(n => name(n).Equals(name(item), StringComparison.OrdinalIgnoreCase))) result.Add(new(name(item), "移除" + kind, value(item), "数据库中不存在"));
            foreach (var item in after)
            {
                var previous = before.FindIndex(p => name(p).Equals(name(item), StringComparison.OrdinalIgnoreCase));
                if (previous < 0) result.Add(new(name(item), "新增" + kind, "设计中不存在", value(item)));
                else Add(name(item), kind, value(before[previous]), value(item));
            }
        }
    }

    private static Dictionary<string, string> ColumnProperties(ColumnDesign c) => new()
    {
        ["类型"] = c.Computed == "" ? c.Type is "time" or "datetime2" or "datetimeoffset" ? $"{c.Type}({c.TemporalScale ?? 7})" : c.Type == "float" ? $"float({c.FloatPrecision ?? 53})" : SqlServerDdl.DataType(c) : "计算列",
        ["允许空值"] = c.Computed == "" ? (c.Nullable ? "是" : "否") : "由表达式决定",
        ["自增"] = c.Identity ? $"{c.IdentitySeed}, {c.IdentityIncrement}" : "否",
        ["默认值"] = Expression(c.Default),
        ["默认约束名称"] = c.Default == "" ? "" : c.DefaultConstraintSystemNamed || c.DefaultConstraintName == "" ? "系统自动命名" : c.DefaultConstraintName,
        ["计算表达式"] = Expression(c.Computed),
        ["持久化"] = c.Computed != "" && c.Persisted ? "是" : "否",
        ["排序规则"] = c.Collation
    };

    private static string PrimaryKey(TableDesign t)
    {
        var keys = t.Columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).ToList();
        if (keys.Count == 0) return "无";
        return JsonSerializer.Serialize(new { Name = t.PrimaryKeySystemNamed ? "系统自动命名" : t.PrimaryKeyName == "" ? "PK_" + t.Name : t.PrimaryKeyName, Columns = string.Join(",", keys.Select(c => c.Name)), Descending = Names(t.PrimaryKeyDescendingColumns, true), Clustered = t.PrimaryKeyClustered ?? !t.Indexes.Any(i => i.Clustered) });
    }

    private static string Names(string value, bool unordered = false) => string.Join(",", unordered ? SqlServerDdl.Names(value).OrderBy(n => n, StringComparer.OrdinalIgnoreCase) : SqlServerDdl.Names(value));

    private static string Expression(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var expression = new TSql160Parser(true).ParseExpression(new StringReader(value), out var errors);
        if (errors.Count > 0) return Tokens(value);
        while (expression is ParenthesisExpression parenthesis) expression = parenthesis.Expression;
        new Sql160ScriptGenerator().GenerateScript(expression, out var normalized);
        return Tokens(normalized);
    }

    // 按词法单元去掉排版差异，保留字符串字面量中的空格和大小写。
    private static string Tokens(string value)
    {
        var tokens = new TSql160Parser(true).GetTokenStream(new StringReader(value), out _);
        return string.Join(" ", tokens.Where(t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)).Select(t => t.Text));
    }
}
