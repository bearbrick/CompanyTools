namespace DbStudio.Core;

/// <summary>
/// 普通索引或 UNIQUE 约束，键列顺序具有语义。
/// </summary>
public class IndexDesign
{
    /// <summary>
    /// 索引或约束名称。
    /// </summary>
    public string Name { get; set; } = "IX_New";

    /// <summary>
    /// 英文逗号分隔的有序键列。
    /// </summary>
    public string Columns { get; set; } = "";

    /// <summary>键列中采用降序的字段名，以英文逗号分隔。</summary>
    public string DescendingColumns { get; set; } = "";

    /// <summary>
    /// 普通非聚集索引的包含列，英文逗号分隔。
    /// </summary>
    public string Include { get; set; } = "";

    /// <summary>
    /// 键值是否必须唯一。
    /// </summary>
    public bool Unique
    {
        get; set;
    }

    /// <summary>
    /// 是否生成为 UNIQUE 约束；必须同时设置 Unique。
    /// </summary>
    public bool IsConstraint
    {
        get; set;
    }

    /// <summary>
    /// 是否为聚集索引；一张表最多一个。
    /// </summary>
    public bool Clustered
    {
        get; set;
    }

    /// <summary>
    /// 普通索引的 WHERE 表达式，不含 WHERE 关键字。
    /// </summary>
    public string Filter { get; set; } = "";
}
