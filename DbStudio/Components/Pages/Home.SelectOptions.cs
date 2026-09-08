using DbStudio.Core;

namespace DbStudio.Components.Pages;

public partial class Home
{
    /// <summary>
    /// 项目选项使用稳定 ID，项目名称变化不会影响当前选择。
    /// </summary>
    private List<SelectOption> ProjectOptions => projects
        .Select(item => new SelectOption(item.Id, item.Name, item.Description)).ToList();

    /// <summary>
    /// 外键选表同时展示逻辑名与架构限定的物理名，两者均可检索。
    /// </summary>
    private List<SelectOption> TableOptions => (project?.Tables ?? [])
        .Select(item => new SelectOption(item.Id, item.Label, $"{item.Schema}.{item.Name}", item.Module)).ToList();

    /// <summary>
    /// 角色说明直观显示权限组合，减少分配账号时的猜测。
    /// </summary>
    private List<SelectOption> RoleOptions => roles.Select(item => new SelectOption(item.Id, item.Name,
        string.Join(" · ", PermissionList.Where(p => item.Permissions.HasFlag(p)).Select(PermissionName)))).ToList();

    /// <summary>
    /// 模块允许选择已有名称，也允许输入新名称。
    /// </summary>
    private List<SelectOption> ModuleOptions => project == null ? [] : ModuleOrdering.Names(project)
        .Select(name => new SelectOption(name, name)).ToList();

    /// <summary>
    /// 外键本表字段建议；允许输入逗号分隔的复合字段列表。
    /// </summary>
    private List<SelectOption> LocalColumnOptions => (table?.Columns ?? [])
        .Select(item => new SelectOption(item.Name, item.Name, item.Label)).ToList();

    /// <summary>
    /// 字段类型目录与 SQL 校验器共享唯一的合法值清单，说明只服务于界面。
    /// </summary>
    private static readonly List<SelectOption> TypeOptions = SqlServerDdl.Types.Select(type =>
    {
        var (description, group) = type switch
        {
            "bigint" => ("64 位整数", "数值"),
            "int" => ("32 位整数", "数值"),
            "smallint" => ("16 位整数", "数值"),
            "tinyint" => ("0–255 的整数", "数值"),
            "bit" => ("布尔值：0 / 1", "数值"),
            "decimal" or "numeric" => ("精确数值，可设置精度和小数位", "数值"),
            "money" or "smallmoney" => ("固定四位小数的货币金额", "数值"),
            "float" or "real" => ("近似浮点数", "数值"),
            "nvarchar" => ("可变长度 Unicode 文本", "字符"),
            "varchar" => ("可变长度字符文本", "字符"),
            "nchar" => ("固定长度 Unicode 文本", "字符"),
            "char" => ("固定长度字符文本", "字符"),
            "varbinary" => ("可变长度二进制数据", "二进制"),
            "binary" => ("固定长度二进制数据", "二进制"),
            "date" => ("仅日期", "日期时间"),
            "time" => ("仅时间", "日期时间"),
            "datetimeoffset" => ("带时区偏移的日期时间", "日期时间"),
            "datetime2" => ("高精度日期时间", "日期时间"),
            "datetime" or "smalldatetime" => ("日期与时间", "日期时间"),
            "uniqueidentifier" => ("全局唯一标识 GUID", "其他"),
            "rowversion" => ("数据库自动生成的版本戳", "其他"),
            "xml" => ("XML 文档", "其他"),
            "sql_variant" => ("可保存多种基础类型的值", "其他"),
            _ => ("兼容现有设计的旧版大对象类型", "旧版类型")
        };
        return new SelectOption(type, type, description, group);
    }).ToList();

    /// <summary>
    /// 展示中文含义，保存时仍使用合法的 SQL Server 参照动作。
    /// </summary>
    private static readonly List<SelectOption> ActionOptions =
    [
        new("NO ACTION", "NO ACTION", "阻止破坏引用关系的操作", "默认"),
        new("CASCADE", "CASCADE", "级联删除或更新关联记录"),
        new("SET NULL", "SET NULL", "关联字段设为空值，字段须允许 NULL"),
        new("SET DEFAULT", "SET DEFAULT", "关联字段恢复默认值")
    ];
}
