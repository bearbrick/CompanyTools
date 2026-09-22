namespace DbStudio.Core;

/// <summary>字段关系搜索中的另一端；方向由所在的 References 或 ReferencedBy 集合确定。</summary>
public sealed record FieldReferenceMatch(string TableId, string TableName, string TableLabel, string ColumnId,
    string ColumnName, string RelationName, bool IsLogical);

/// <summary>可从顶部快速检索并定位的字段索引项。</summary>
public sealed record ProjectFieldSearchEntry(string TableId, string TableName, string TableLabel, string Module,
    string ColumnId, string ColumnName, string ColumnLabel, string Type, int SameNameCount,
    IReadOnlyList<FieldReferenceMatch> References, IReadOnlyList<FieldReferenceMatch> ReferencedBy, string SearchText);

/// <summary>字段搜索结果；Total 保留截断前数量，便于界面提示继续缩小范围。</summary>
public sealed record ProjectFieldSearchResponse(IReadOnlyList<ProjectFieldSearchEntry> Items, int Total);

/// <summary>
/// 基于单个已保存项目修订建立的轻量字段索引。项目切换或修订变化时重建，不引入外部搜索服务。
/// </summary>
public sealed class ProjectFieldSearchIndex
{
    private readonly IReadOnlyList<ProjectFieldSearchEntry> entries;

    private ProjectFieldSearchIndex(IReadOnlyList<ProjectFieldSearchEntry> entries)
    {
        this.entries = entries;
    }

    /// <summary>建立字段文本、同名统计、正向引用和反向引用索引。</summary>
    public static ProjectFieldSearchIndex Build(DesignProject project)
    {
        var tables = project.Tables.ToDictionary(table => table.Id, StringComparer.Ordinal);
        var outgoing = new Dictionary<(string TableId, string ColumnId), List<FieldReferenceMatch>>();
        var incoming = new Dictionary<(string TableId, string ColumnId), List<FieldReferenceMatch>>();

        foreach (var source in project.Tables)
        {
            foreach (var relation in source.ForeignKeys)
            {
                if (!tables.TryGetValue(relation.TargetTableId, out var target))
                {
                    continue;
                }

                var localNames = SqlServerDdl.Names(relation.Columns);
                var targetNames = SqlServerDdl.Names(relation.TargetColumns);
                for (var index = 0; index < Math.Min(localNames.Length, targetNames.Length); index++)
                {
                    var local = source.Columns.FirstOrDefault(column => column.Name.Equals(localNames[index], StringComparison.OrdinalIgnoreCase));
                    var remote = target.Columns.FirstOrDefault(column => column.Name.Equals(targetNames[index], StringComparison.OrdinalIgnoreCase));
                    if (local == null || remote == null)
                    {
                        continue;
                    }

                    Add(outgoing, (source.Id, local.Id), new(target.Id, target.Name, target.Label, remote.Id,
                        remote.Name, relation.Name, relation.IsLogical));
                    Add(incoming, (target.Id, remote.Id), new(source.Id, source.Name, source.Label, local.Id,
                        local.Name, relation.Name, relation.IsLogical));
                }
            }
        }

        var sameNames = project.Tables.SelectMany(table => table.Columns)
            .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var items = project.Tables.SelectMany(table => table.Columns.Select(column =>
            new ProjectFieldSearchEntry(table.Id, table.Name, table.Label, table.Module, column.Id, column.Name,
                column.Label, SqlServerDdl.DataType(column), sameNames[column.Name],
                outgoing.GetValueOrDefault((table.Id, column.Id)) ?? [], incoming.GetValueOrDefault((table.Id, column.Id)) ?? [],
                $"{column.Name} {column.Label} {table.Name} {table.Label} {table.Schema} {table.Module} {SqlServerDdl.DataType(column)} {column.Comment}")))
            .ToList();
        return new(items);
    }

    /// <summary>所有空格分隔词都须匹配；字段精确命中优先，其次为定义名、前缀、表和备注。</summary>
    public ProjectFieldSearchResponse Search(string query, int limit = 12)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var normalized = query.Trim();
        if (normalized == "")
        {
            return new([], 0);
        }

        var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = entries.Where(entry => terms.All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => (Entry: entry, Score: Score(entry, normalized, terms)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.ColumnName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Entry.TableName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(matches.Take(limit).Select(item => item.Entry).ToList(), matches.Count);
    }

    private static int Score(ProjectFieldSearchEntry entry, string query, IReadOnlyList<string> terms)
    {
        var score = Match(entry.ColumnName, query, 1000, 750, 520)
            + Match(entry.ColumnLabel, query, 900, 680, 480)
            + Match(entry.TableName, query, 600, 430, 300)
            + Match(entry.TableLabel, query, 540, 390, 270);
        foreach (var term in terms)
        {
            score += Match(entry.ColumnName, term, 160, 120, 80)
                + Match(entry.ColumnLabel, term, 140, 100, 70)
                + Match(entry.TableName, term, 90, 70, 50)
                + Match(entry.TableLabel, term, 80, 60, 40);
        }
        return score + (entry.References.Count + entry.ReferencedBy.Count) * 3;
    }

    private static int Match(string value, string query, int exact, int prefix, int contains)
    {
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return exact;
        }
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return prefix;
        }
        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? contains : 0;
    }

    private static void Add(Dictionary<(string TableId, string ColumnId), List<FieldReferenceMatch>> index,
        (string TableId, string ColumnId) key, FieldReferenceMatch value)
    {
        if (!index.TryGetValue(key, out var values))
        {
            values = [];
            index.Add(key, values);
        }
        values.Add(value);
    }
}
