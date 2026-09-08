namespace DbStudio.Core;

/// <summary>
/// 一张数据表的完整设计，包含字段及表级约束。
/// </summary>
public class TableDesign
{
    /// <summary>
    /// 设计内部稳定标识，外键通过此 ID 引用表。
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 数据库架构名，默认 dbo。
    /// </summary>
    public string Schema { get; set; } = "dbo";

    /// <summary>
    /// 物理表名。
    /// </summary>
    public string Name { get; set; } = "NewTable";

    /// <summary>
    /// 业务显示名称。
    /// </summary>
    public string Label { get; set; } = "新数据表";

    /// <summary>
    /// 左侧清单使用的业务模块。
    /// </summary>
    public string Module { get; set; } = "未分组";

    /// <summary>
    /// 表设计补充说明，仅保存在 Studio，不写入数据库 MS_Description。
    /// </summary>
    public string Comment { get; set; } = "";

    /// <summary>主键约束名，留空时使用 PK_表名。</summary>
    public string PrimaryKeyName { get; set; } = "";
    /// <summary>实际库主键是否由系统命名；保留匿名声明以匹配 DacFx 提取模型。</summary>
    public bool PrimaryKeySystemNamed { get; set; }
    /// <summary>主键是否聚集，空值时自动根据其他索引推导。</summary>
    public bool? PrimaryKeyClustered
    {
        get; set;
    }
    /// <summary>主键中按降序排列的字段名，以英文逗号分隔。</summary>
    public string PrimaryKeyDescendingColumns { get; set; } = "";

    /// <summary>
    /// 按设计顺序保存的字段列表。
    /// </summary>
    public List<ColumnDesign> Columns { get; set; } = [];

    /// <summary>
    /// 普通索引和唯一约束。
    /// </summary>
    public List<IndexDesign> Indexes { get; set; } = [];

    /// <summary>
    /// 指向项目内数据表的外键。
    /// </summary>
    public List<ForeignKeyDesign> ForeignKeys { get; set; } = [];

    /// <summary>
    /// 表级 CHECK 约束。
    /// </summary>
    public List<CheckDesign> Checks { get; set; } = [];
}
