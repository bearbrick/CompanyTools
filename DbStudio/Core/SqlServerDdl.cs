namespace DbStudio.Core;

/// <summary>
/// SQL Server 方言的类型目录和转义工具。所有 SQL 标识符与文字统一在此编码。
/// </summary>
public static partial class SqlServerDdl
{
    /// <summary>
    /// 支持的数据类型目录。
    /// </summary>
    public static readonly string[] Types = ["bigint", "int", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real", "nvarchar", "varchar", "nchar", "char", "varbinary", "binary", "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time", "uniqueidentifier", "xml", "rowversion", "ntext", "text", "image", "sql_variant"];

    /// <summary>
    /// 外键参照动作白名单。
    /// </summary>
    public static readonly string[] Actions = ["NO ACTION", "CASCADE", "SET NULL", "SET DEFAULT"];

    /// <summary>
    /// 解析逗号分隔的有序字段列表。
    /// </summary>
    public static string[] Names(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 引用并转义 SQL 标识符。
    /// </summary>
    public static string Q(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>
    /// 生成 Unicode SQL 字符串字面量，转义单引号。
    /// </summary>
    private static string Lit(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// 生成带标识符转义的有序字段列表。
    /// </summary>
    private static string List(string value) => string.Join(", ", Names(value).Select(Q));

    /// <summary>
    /// 判断类型是否接受长度参数。
    /// </summary>
    public static bool HasLength(string type) => new[] { "nvarchar", "varchar", "nchar", "char", "binary", "varbinary" }.Contains(type);

    /// <summary>
    /// 根据类型、长度及精度生成字段类型声明。
    /// </summary>
    public static string DataType(ColumnDesign c) => HasLength(c.Type) ? $"{c.Type}({c.Length})" : c.Type is "decimal" or "numeric" ? $"{c.Type}({c.Precision},{c.Scale})"
        : c.Type is "time" or "datetime2" or "datetimeoffset" && c.TemporalScale.HasValue ? $"{c.Type}({c.TemporalScale})"
        : c.Type == "float" && c.FloatPrecision.HasValue ? $"float({c.FloatPrecision})" : c.Type;

    /// <summary>按有序键列生成升降序，降序列表必须经过结构校验。</summary>
    private static string KeyList(string columns, string descending) => string.Join(", ", Names(columns).Select(n => Q(n) + (Names(descending).Contains(n, StringComparer.OrdinalIgnoreCase) ? " DESC" : "")));

}
