namespace DbStudio.Core;

/// <summary>一次已保存的设计修订；Document 仅在服务端恢复时读取。</summary>
public sealed record ProjectRevisionInfo(int Revision, string Time, string Actor, string Source, string Action,
    string Summary, int? ReleaseVersion, int? RestoredFromRevision);

/// <summary>一个经整库校验的不可变发布版本。</summary>
public sealed record ProjectReleaseInfo(int Version, int SourceRevision, string Time, string Actor, string DocumentHash);

/// <summary>项目修订与发布版本摘要。</summary>
public sealed record ProjectVersionSummary(int CurrentRevision, int LatestReleaseVersion, int? LatestReleaseRevision,
    int TotalRevisionCount, IReadOnlyList<ProjectRevisionInfo> Revisions, IReadOnlyList<ProjectReleaseInfo> Releases);

/// <summary>两个修订之间的一项可读变化。</summary>
public sealed record ProjectDiffItem(string Scope, string Kind, string ObjectName, string Before, string After);

/// <summary>修订差异；ToRevision 为 null 时表示与当前版本比较。</summary>
public sealed record ProjectRevisionDiff(int FromRevision, int ToRevision, IReadOnlyList<ProjectDiffItem> Items);

/// <summary>某个已保存连接的最近发布状态；密码与连接串永不进入记录。</summary>
public sealed record DatabaseVersionStatus(string ConnectionId, string ConnectionName, string Environment, string Server, string Database,
    string Status, int ProjectRevision, int? ReleaseVersion, string Scope, string StartedAt, string CompletedAt, string Detail);

/// <summary>同步执行结果，区分整库已发布和局部同步。</summary>
public sealed record DatabaseDeploymentResult(bool FullyVerified, int? ReleaseVersion, int ProjectRevision, string Message);

/// <summary>不可变 V 在环境链路中的推进约束。</summary>
public static class ReleasePromotionPolicy
{
    /// <summary>返回阻止发布的原因；空字符串表示允许推进。</summary>
    public static string BlockReason(string targetConnectionId, string targetEnvironment, int releaseVersion,
        IReadOnlyList<DatabaseVersionStatus> statuses)
    {
        var targetOrder = DatabaseEnvironments.Order(targetEnvironment);
        if (targetOrder <= DatabaseEnvironments.Order(DatabaseEnvironments.Development))
        {
            return "";
        }

        var lower = statuses
            .Where(item => item.ConnectionId != targetConnectionId
                && DatabaseEnvironments.Order(item.Environment) < targetOrder)
            .ToList();
        if (lower.Count == 0)
        {
            return $"V{releaseVersion} 尚未经过较低环境，请先配置并发布到开发环境。";
        }

        var blocked = lower.Where(item => item.Status != "succeeded" || item.ReleaseVersion != releaseVersion).ToList();
        if (blocked.Count == 0)
        {
            return "";
        }

        var targets = string.Join("、", blocked.Select(item => $"{DatabaseEnvironments.Name(item.Environment)}：{item.ConnectionName}"));
        return $"V{releaseVersion} 尚未在所有较低环境验证通过：{targets}。";
    }
}

internal sealed record RevisionCommitInfo(string Actor, string Source, string Action, string Summary, int? RestoredFromRevision = null)
{
    internal static readonly RevisionCommitInfo Design = new("", "design", "保存设计", "");
}

/// <summary>基于稳定 ID 比较项目；序列化对比用于覆盖约束细节。</summary>
public static class ProjectVersionDiffEngine
{
    /// <summary>基于稳定 ID 比较两个项目快照，可选择只返回一张表的变化。</summary>
    public static ProjectRevisionDiff Compare(DesignProject before, DesignProject after, string? tableId = null)
    {
        var items = new List<ProjectDiffItem>();
        if (tableId == null)
        {
            Add(items, "项目", "修改", "名称", before.Name, after.Name);
            Add(items, "项目", "修改", "说明", before.Description, after.Description);
            Add(items, "项目", "修改", "模块顺序", string.Join(" → ", before.Modules), string.Join(" → ", after.Modules));
        }

        var oldTables = StableMap(before.Tables, table => table.Id, "表");
        var newTables = StableMap(after.Tables, table => table.Id, "表");
        foreach (var id in oldTables.Keys.Union(newTables.Keys).Where(id => tableId == null || id == tableId))
        {
            oldTables.TryGetValue(id, out var oldTable);
            newTables.TryGetValue(id, out var newTable);
            if (oldTable == null)
            {
                items.Add(new("表", "新增", Name(newTable!), "", TableSummary(newTable!)));
                continue;
            }
            if (newTable == null)
            {
                items.Add(new("表", "删除", Name(oldTable), TableSummary(oldTable), ""));
                continue;
            }
            CompareTable(items, oldTable, newTable);
        }
        return new(before.Revision, after.Revision, items);
    }

    private static void CompareTable(List<ProjectDiffItem> items, TableDesign before, TableDesign after)
    {
        var scope = $"表 {Name(after)}";
        Add(items, scope, "修改", "物理名", $"{before.Schema}.{before.Name}", $"{after.Schema}.{after.Name}");
        Add(items, scope, "修改", "中文名", before.Label, after.Label);
        Add(items, scope, "修改", "模块", before.Module, after.Module);
        Add(items, scope, "修改", "数据分类", before.DataCategory, after.DataCategory);
        Add(items, scope, "修改", "备注", before.Comment, after.Comment);
        Add(items, scope, "修改", "主键", $"{before.PrimaryKeyName}|{before.PrimaryKeyClustered}|{before.PrimaryKeyDescendingColumns}", $"{after.PrimaryKeyName}|{after.PrimaryKeyClustered}|{after.PrimaryKeyDescendingColumns}");

        var oldColumns = StableMap(before.Columns, column => column.Id, "字段");
        var newColumns = StableMap(after.Columns, column => column.Id, "字段");
        var oldPositions = before.Columns.Select((column, index) => (column.Id, Position: index + 1)).ToDictionary(item => item.Id, item => item.Position);
        var newPositions = after.Columns.Select((column, index) => (column.Id, Position: index + 1)).ToDictionary(item => item.Id, item => item.Position);
        foreach (var id in oldColumns.Keys.Union(newColumns.Keys))
        {
            oldColumns.TryGetValue(id, out var oldColumn);
            newColumns.TryGetValue(id, out var newColumn);
            if (oldColumn == null)
            {
                items.Add(new(scope, "新增字段", newColumn!.Name, "", ColumnSummary(newColumn)));
                continue;
            }
            if (newColumn == null)
            {
                items.Add(new(scope, "删除字段", oldColumn.Name, ColumnSummary(oldColumn), ""));
                continue;
            }
            var oldSummary = $"#{oldPositions[id]} · {ColumnSummary(oldColumn)}";
            var newSummary = $"#{newPositions[id]} · {ColumnSummary(newColumn)}";
            if (oldSummary != newSummary)
            {
                items.Add(new(scope, "修改字段", newColumn.Name, oldSummary, newSummary));
            }
        }
        CompareNamed(items, scope, "索引", NamedMap(before.Indexes, index => index.Name), NamedMap(after.Indexes, index => index.Name), IndexSummary);
        CompareNamed(items, scope, "表关系", NamedMap(before.ForeignKeys, key => key.Name), NamedMap(after.ForeignKeys, key => key.Name), RelationshipSummary);
        CompareNamed(items, scope, "CHECK", NamedMap(before.Checks, check => check.Name), NamedMap(after.Checks, check => check.Name), check => check.Expression);
    }

    private static void CompareNamed<T>(List<ProjectDiffItem> items, string scope, string kind, Dictionary<string, T> before, Dictionary<string, T> after, Func<T, string> describe)
    {
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(name, out var oldValue);
            after.TryGetValue(name, out var newValue);
            var oldText = oldValue == null ? "" : describe(oldValue);
            var newText = newValue == null ? "" : describe(newValue);
            if (oldText != newText)
            {
                items.Add(new(scope, oldValue == null ? $"新增{kind}" : newValue == null ? $"删除{kind}" : $"修改{kind}", name, oldText, newText));
            }
        }
    }

    private static void Add(List<ProjectDiffItem> items, string scope, string kind, string name, string before, string after)
    {
        if (before != after)
        {
            items.Add(new(scope, kind, name, before, after));
        }
    }

    private static Dictionary<string, T> StableMap<T>(IEnumerable<T> values, Func<T, string> id, string objectType)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var key = id(value);
            if (!result.TryAdd(key, value))
            {
                throw new InvalidOperationException($"历史快照包含重复的{objectType} ID，无法可靠比对。");
            }
        }
        return result;
    }

    private static Dictionary<string, T> NamedMap<T>(IEnumerable<T> values, Func<T, string> name)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var baseName = name(value);
            var key = baseName;
            var occurrence = 2;
            while (!result.TryAdd(key, value))
            {
                key = $"{baseName} #{occurrence++}";
            }
        }
        return result;
    }

    private static string Name(TableDesign table) => $"{table.Label} ({table.Schema}.{table.Name})";
    private static string TableSummary(TableDesign table) => $"{table.Columns.Count} 字段，{table.Indexes.Count} 索引，{table.ForeignKeys.Count} 关系";
    private static string ColumnSummary(ColumnDesign c) => $"{c.Name} / {c.Label} · {c.Type}({c.Length},{c.Precision},{c.Scale}) · {(c.Nullable ? "NULL" : "NOT NULL")} · PK {c.PrimaryKeyOrder} · IDENTITY {c.Identity} · DEFAULT {c.Default} · COMPUTED {c.Computed} · 备注 {c.Comment}";
    private static string IndexSummary(IndexDesign index) => $"{index.Columns} · DESC {index.DescendingColumns} · INCLUDE {index.Include} · UNIQUE {index.Unique} · CONSTRAINT {index.IsConstraint} · CLUSTERED {index.Clustered} · WHERE {index.Filter}";
    private static string RelationshipSummary(ForeignKeyDesign key) => $"{(key.IsLogical ? "逻辑" : "物理")} · {key.Columns} → {key.TargetTableId}:{key.TargetColumns} · DELETE {key.OnDelete} · UPDATE {key.OnUpdate}";
}
