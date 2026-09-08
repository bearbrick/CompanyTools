namespace DbStudio.Core;

/// <summary>复制表时允许修改的基本信息；字段和约束配置始终取自源表。</summary>
public sealed class TableCopyOptions
{
    /// <summary>新表所属数据库架构。</summary>
    public string Schema { get; set; } = "dbo";

    /// <summary>新表的物理名称，同一架构内必须唯一。</summary>
    public string Name { get; set; } = "";

    /// <summary>新表的业务定义名。</summary>
    public string Label { get; set; } = "";

    /// <summary>左侧表清单中的所属模块。</summary>
    public string Module { get; set; } = "未分组";

    /// <summary>仅保留在 Studio 中的表级业务备注。</summary>
    public string Comment { get; set; } = "";
}
