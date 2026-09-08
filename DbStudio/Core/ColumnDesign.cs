namespace DbStudio.Core;

/// <summary>
/// 字段定义及其 DDL 属性，保留 Excel 中的业务说明。
/// </summary>
public class ColumnDesign
{
    /// <summary>
    /// 字段内部标识，供编辑器跟踪选择和顺序。
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 物理字段名。
    /// </summary>
    public string Name { get; set; } = "NewColumn";

    /// <summary>
    /// 业务定义名，写入扩展属性。
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// SQL Server 基础类型，不包含长度或精度。
    /// </summary>
    public string Type { get; set; } = "nvarchar";

    /// <summary>
    /// 字符或二进制长度；变长类型支持 max。
    /// </summary>
    public string Length { get; set; } = "50";

    /// <summary>
    /// decimal / numeric 的总位数，范围 1–38。
    /// </summary>
    public int Precision { get; set; } = 18;

    /// <summary>
    /// decimal / numeric 的小数位数，不能超过精度。
    /// </summary>
    public int Scale { get; set; } = 2;

    /// <summary>时间类型的小数秒精度，空值表示 SQL Server 默认值。</summary>
    public int? TemporalScale
    {
        get; set;
    }

    /// <summary>float 有效位数，空值表示 SQL Server 默认值。</summary>
    public int? FloatPrecision
    {
        get; set;
    }

    /// <summary>
    /// 是否允许 NULL。
    /// </summary>
    public bool Nullable { get; set; } = true;

    /// <summary>
    /// 复合主键的顺序；0 表示不属于主键。
    /// </summary>
    public int PrimaryKeyOrder
    {
        get; set;
    }

    /// <summary>
    /// 是否启用 IDENTITY 自增。
    /// </summary>
    public bool Identity
    {
        get; set;
    }

    /// <summary>
    /// 自增起始值。
    /// </summary>
    public long IdentitySeed { get; set; } = 1;

    /// <summary>
    /// 自增步长，不允许为 0。
    /// </summary>
    public long IdentityIncrement { get; set; } = 1;

    /// <summary>
    /// 默认值 SQL 表达式，不包含 DEFAULT 关键字。
    /// </summary>
    public string Default { get; set; } = "";

    /// <summary>
    /// 计算列表达式；留空表示普通字段。
    /// </summary>
    public string Computed { get; set; } = "";

    /// <summary>
    /// 计算列是否持久化，需同时提供计算表达式。
    /// </summary>
    public bool Persisted
    {
        get; set;
    }

    /// <summary>
    /// 字符排序规则；留空使用数据库默认规则。
    /// </summary>
    public string Collation { get; set; } = "";

    /// <summary>
    /// 业务输入位数，仅作为设计元数据保留。
    /// </summary>
    public string InputLimit { get; set; } = "";

    /// <summary>
    /// 字段备注，写入扩展属性。
    /// </summary>
    public string Comment { get; set; } = "";
}
