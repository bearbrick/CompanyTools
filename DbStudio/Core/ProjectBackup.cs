using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace DbStudio.Core;

/// <summary>备份预览。错误列表非空时只能查看，不能导入。</summary>
/// <param name="Project">已解析并归一化的设计快照。</param>
/// <param name="Errors">导入前需要修复的结构错误。</param>
public record ProjectImportPreview(DesignProject Project, List<string> Errors);

/// <summary>
/// 项目 JSON 备份的解析和完整性检查。外部文档只作为数据处理，不执行其中的表达式。
/// </summary>
public static class ProjectBackup
{
    /// <summary>单份备份上限为 10 MiB，文件读取和服务端提交采用相同限制。</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    // 仅下载备份使用可读 Unicode；保留 JSON 必需及 HTML 敏感字符的转义，
    // 不改变数据库存储、快照比较和页面内嵌 JSON 的既有序列化规则。
    private static readonly JsonSerializerOptions ExportOptions = new(ModelJson.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>导出缩进排版的项目 JSON，中文直接显示，仍兼容原有备份导入格式。</summary>
    public static string Serialize(DesignProject project) => JsonSerializer.Serialize(project, ExportOptions);

    private static readonly JsonSerializerOptions ImportOptions = new(ModelJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    /// <summary>
    /// 解析本系统导出的项目备份。拒绝未知属性、重复键、空对象成员及缺少关键属性的数据，
    /// 避免反序列化默认值把损坏的备份伪装为有效设计。
    /// </summary>
    /// <param name="json">项目备份 JSON 文本。</param>
    /// <returns>归一化快照和完整结构错误清单。</returns>
    public static ProjectImportPreview Inspect(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new InvalidOperationException("请选择或粘贴不超过 10 MB 的项目 JSON 备份。");
        }

        DesignProject project;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            ValidateJson(document.RootElement);
            RequireProperties(document.RootElement, "Name", "Dialect", "Tables");
            foreach (var table in document.RootElement.EnumerateObject().First(p => p.Name.Equals("Tables", StringComparison.OrdinalIgnoreCase)).Value.EnumerateArray())
            {
                RequireProperties(table, "Id", "Name", "Schema", "Columns");
                foreach (var column in table.EnumerateObject().First(p => p.Name.Equals("Columns", StringComparison.OrdinalIgnoreCase)).Value.EnumerateArray())
                {
                    RequireProperties(column, "Id", "Name", "Type");
                }
            }
            project = JsonSerializer.Deserialize<DesignProject>(json, ImportOptions)!;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("备份格式不正确或包含不支持的属性，请使用 DB Studio 导出的项目 JSON。");
        }
        catch (InvalidOperationException exception) when (!exception.Message.StartsWith("备份"))
        {
            throw new InvalidOperationException("备份的对象或列表格式不正确，请检查 JSON 内容。");
        }

        var errors = new List<string>();
        if (project.Dialect != "SqlServer")
        {
            errors.Add("当前仅支持 SqlServer 项目备份。");
        }

        if (string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 100)
        {
            errors.Add("原项目名称必填且不能超过 100 字符。");
        }

        if (project.Tables.Count > 500 || project.Tables.Sum(t => t.Columns.Count) > 10000
            || project.Tables.Any(t => t.Indexes.Count + t.ForeignKeys.Count + t.Checks.Count > 500))
        {
            throw new InvalidOperationException("备份超过当前导入规模限制：500 张表、10000 个字段、每表 500 个约束或索引。");
        }
        if (project.Tables.Any(t => string.IsNullOrWhiteSpace(t.Id))
            || project.Tables.Select(t => t.Id).Distinct().Count() != project.Tables.Count)
        {
            errors.Add("表 ID 为空或重复，无法可靠恢复外键引用。");
        }
        foreach (var table in project.Tables)
        {
            if (table.Columns.Any(c => string.IsNullOrWhiteSpace(c.Id))
                || table.Columns.Select(c => c.Id).Distinct().Count() != table.Columns.Count)
            {
                errors.Add($"{table.Name}：字段 ID 为空或重复。");
            }
        }
        try
        {
            ModelJson.NormalizeImport(project);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("备份中的字段精度超出有效整数范围。");
        }
        foreach (var table in project.Tables)
        {
            errors.AddRange(SqlServerDdl.Validate(project, table).Select(error => table.Name + " / " + error));
        }
        return new(project, errors);
    }

    /// <summary>拒绝重复键及 null，确保所有后续模型遍历均建立在明确的数据形状上。</summary>
    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException("备份不能包含 null 成员，请使用空字符串或空列表。");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException("备份中存在重复的属性：" + property.Name);
                }

                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateJson(item);
            }
        }
    }

    /// <summary>核验关键属性确实存在，不用模型默认值代替缺失的备份内容。</summary>
    private static void RequireProperties(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("备份中预期的对象格式不正确。");
        }

        var names = element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (required.Any(name => !names.Contains(name)))
        {
            throw new InvalidOperationException("备份缺少必要属性：" + string.Join("、", required.Where(name => !names.Contains(name))));
        }
    }
}
