namespace DbStudio.Core;

/// <summary>表数据用途标签。代码值写入项目文档，中文名称只用于展示。</summary>
public sealed record TableDataCategory(string Id, string Name, string Description, bool Caution = false);

/// <summary>统一维护可选标签，避免设计器、归档和数据清理各自解释。</summary>
public static class TableDataCategories
{
    public const string Unclassified = "unclassified";
    public const string Business = "business";
    public const string Master = "master";
    public const string System = "system";
    public const string Log = "log";
    public const string Temporary = "temporary";

    public static readonly IReadOnlyList<TableDataCategory> All =
    [
        new(Unclassified, "未分类", "尚未确认数据用途；建议分类后再批量操作", true),
        new(Business, "业务数据", "订单、单据、流程等日常业务记录"),
        new(Master, "基础数据", "客户、物料、组织等主数据", true),
        new(System, "系统数据", "配置、权限、序列等系统运行数据", true),
        new(Log, "日志审计", "日志、轨迹和审计记录"),
        new(Temporary, "临时 / 缓存", "可重建的临时结果、缓存或中间数据")
    ];

    public static bool IsValid(string? id) => All.Any(item => item.Id == id);

    public static string Normalize(string? id) => IsValid(id) ? id! : Unclassified;

    public static TableDataCategory Get(string? id) => All.First(item => item.Id == Normalize(id));
}
