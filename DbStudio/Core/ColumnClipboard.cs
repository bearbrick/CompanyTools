using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace DbStudio.Core;

/// <summary>字段剪贴板格式和独立副本生成。剪贴板文本只是设计数据，不执行其中的表达式。</summary>
public sealed class ColumnClipboard
{
    private List<ColumnDesign> snapshot = [];

    /// <summary>剪贴板文本可接受的最大字符数。</summary>
    public const int MaxCharacters = 250_000;
    private static readonly JsonSerializerOptions ClipboardOptions = new(ModelJson.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private sealed record Payload(string Format, int Version, List<ColumnDesign> Columns);
    /// <summary>当前已冻结的字段数量。</summary>
    public int Count => snapshot.Count;

    /// <summary>按来源表的字段顺序冻结选择；后续编辑来源不会改变已复制的内容。</summary>
    public void Capture(IEnumerable<ColumnDesign> columns, IReadOnlySet<string> selected)
    {
        snapshot = columns.Where(column => selected.Contains(column.Id)).Select(ModelJson.Clone).ToList();
    }

    /// <summary>写入带格式标识和版本的 JSON，支持跨表、跨项目及不同工作台页面粘贴。</summary>
    public string Serialize()
    {
        var text = JsonSerializer.Serialize(new Payload("DbStudio.Columns", 1, snapshot), ClipboardOptions);
        CheckSize(text);
        return text;
    }

    /// <summary>完整校验格式后才接受快照；错误内容不会部分追加到目标表。</summary>
    public static ColumnClipboard Parse(string text)
    {
        CheckSize(text);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Columns", out var columns) || columns.ValueKind != JsonValueKind.Array
                || columns.EnumerateArray().Any(column => column.ValueKind != JsonValueKind.Object
                    || !column.TryGetProperty("Name", out _) || !column.TryGetProperty("Type", out _)))
            {
                throw new JsonException();
            }
            var payload = JsonSerializer.Deserialize<Payload>(text, ClipboardOptions);
            if (payload?.Format != "DbStudio.Columns" || payload.Version != 1 || payload.Columns is not { Count: > 0 and <= 1024 }
                || payload.Columns.Any(column => column == null || string.IsNullOrWhiteSpace(column.Name) || column.Name.Length > 128
                    || !SqlServerDdl.Types.Contains(column.Type)
                    || typeof(ColumnDesign).GetProperties().Any(property => property.PropertyType == typeof(string) && property.GetValue(column) == null)))
            {
                throw new JsonException();
            }
            return new ColumnClipboard { snapshot = payload.Columns };
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("剪贴板内容不是有效的 DB Studio 字段定义，请先选中字段并点击「批量复制」。");
        }
    }

    private static void CheckSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCharacters || Encoding.UTF8.GetByteCount(text) > 1024 * 1024)
        {
            throw new InvalidOperationException("字段剪贴板内容为空或过大，请减少选择的字段后重新复制。");
        }
    }

    /// <summary>
    /// 为每次粘贴生成独立字段，不修改来源及目标。保留字段属性，清除主键、自增和默认约束标识。
    /// 重名自动添加 _copy、_copy2 等后缀；保留其他待粘贴字段的原名称，并遵守 SQL Server 标识符长度限制。
    /// </summary>
    public List<ColumnDesign> CreateCopies(IReadOnlyList<ColumnDesign> target)
    {
        var occupied = target.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reserved = snapshot.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<ColumnDesign>();
        foreach (var source in snapshot)
        {
            var copy = ModelJson.Clone(source);
            copy.Id = Guid.NewGuid().ToString("N");
            if (occupied.Contains(copy.Name))
            {
                for (var number = 1; ; number++)
                {
                    var suffix = number == 1 ? "_copy" : "_copy" + number;
                    var candidate = source.Name[..Math.Min(source.Name.Length, 128 - suffix.Length)] + suffix;
                    if (!occupied.Contains(candidate) && !reserved.Contains(candidate))
                    {
                        copy.Name = candidate;
                        break;
                    }
                }
            }
            occupied.Add(copy.Name);
            copy.PrimaryKeyOrder = 0;
            copy.Identity = false;
            copy.DefaultConstraintName = "";
            copy.DefaultConstraintSystemNamed = false;
            result.Add(copy);
        }
        return result;
    }
}
