namespace DbStudio.Core;

/// <summary>表数据用途标签。代码值写入项目文档，中文名称只用于展示。</summary>
public sealed record TableDataCategory(string Id, string Name, string Description, bool Caution = false);

/// <summary>统一维护可选标签，避免设计器、归档和数据清理各自解释。</summary>
public static class TableDataCategories
{
    /// <summary>未完成业务分类的安全默认值。</summary>
    public const string Unclassified = "unclassified";
    /// <summary>日常交易、单据和流程数据。</summary>
    public const string Business = "business";
    /// <summary>客户、物料和组织等基础数据。</summary>
    public const string Master = "master";
    /// <summary>系统配置、权限和序列数据。</summary>
    public const string System = "system";
    /// <summary>日志、轨迹和审计数据。</summary>
    public const string Log = "log";
    /// <summary>可重建的临时结果或缓存数据。</summary>
    public const string Temporary = "temporary";

    /// <summary>全部可选数据用途，顺序即界面展示顺序。</summary>
    public static readonly IReadOnlyList<TableDataCategory> All =
    [
        new(Unclassified, "未分类", "尚未确认数据用途；建议分类后再批量操作", true),
        new(Business, "业务数据", "订单、单据、流程等日常业务记录"),
        new(Master, "基础数据", "客户、物料、组织等主数据", true),
        new(System, "系统数据", "配置、权限、序列等系统运行数据", true),
        new(Log, "日志审计", "日志、轨迹和审计记录"),
        new(Temporary, "临时 / 缓存", "可重建的临时结果、缓存或中间数据")
    ];

    /// <summary>判断代码是否属于已知数据用途。</summary>
    public static bool IsValid(string? id) => All.Any(item => item.Id == id);

    /// <summary>把空值或未知代码安全归入未分类。</summary>
    public static string Normalize(string? id) => IsValid(id) ? id! : Unclassified;

    /// <summary>取得已归一化的数据用途定义。</summary>
    public static TableDataCategory Get(string? id) => All.First(item => item.Id == Normalize(id));
}
