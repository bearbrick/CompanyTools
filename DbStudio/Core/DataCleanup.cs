namespace DbStudio.Core;

/// <summary>清空计划中一张经过实际数据库核验的表。</summary>
public sealed record DataCleanupTable(string TableId, string Schema, string Name, string Label, string Category, bool ResetsIdentity)
{
    public string DisplayName => $"{Schema}.{Name}";
}

/// <summary>一次性数据清空计划；展示脚本可下载，执行仍使用服务端冻结的同一份脚本。</summary>
public sealed record DataCleanupPlan(string Id, string Database, int ProjectRevision, DateTimeOffset ExpiresAt,
    IReadOnlyList<DataCleanupTable> Tables, string Script);
