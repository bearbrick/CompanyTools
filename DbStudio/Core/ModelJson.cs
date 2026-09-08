using System.Security.Claims;
using System.Text.Json;

namespace DbStudio.Core;

/// <summary>
/// 项目快照序列化、深复制与旧版导入兼容处理。
/// </summary>
public static class ModelJson
{
    /// <summary>
    /// 项目文档统一使用的序列化选项。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// 通过 JSON 深复制编辑快照，避免未保存修改污染已保存对象。
    /// </summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    /// <summary>
    /// 读取身份声明中的用户标识，缺失时返回空字符串。
    /// </summary>
    public static string UserId(this ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>
    /// 兼容旧 Excel 的 decimal(p,s) 类型写法及 -1 表示 max 的长度。
    /// </summary>
    public static DesignProject NormalizeImport(DesignProject project)
    {
        foreach (var c in project.Tables.SelectMany(t => t.Columns))
        {
            var match = System.Text.RegularExpressions.Regex.Match(c.Type, @"^(decimal|numeric)\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                c.Type = match.Groups[1].Value.ToLowerInvariant();
                c.Precision = int.Parse(match.Groups[2].Value);
                c.Scale = int.Parse(match.Groups[3].Value);
            }
            if (c.Length == "-1" && c.Type is "nvarchar" or "varchar" or "varbinary")
            {
                c.Length = "max";
            }
        }
        return project;
    }
}
