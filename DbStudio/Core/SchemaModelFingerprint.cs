using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace DbStudio.Core;

/// <summary>
/// 对 DacFx 的结构定义生成稳定指纹。提取产生的 SQL 登录密码占位值不属于设计器维护范围，
/// 每次提取可能不同；其余定义保留，不能因此跳过执行前的真实结构漂移检查。
/// </summary>
public static class SchemaModelFingerprint
{
    /// <summary>忽略包元信息、模型顶层对象枚举顺序和登录密码占位值，保留字段及键列的内部顺序。</summary>
    public static string Create(byte[] package)
    {
        using var stream = new MemoryStream(package);
        using var archive = new ZipArchive(stream);
        using var input = (archive.GetEntry("model.xml")
            ?? throw new InvalidOperationException("数据库结构快照缺少 model.xml，请重新比对。")).Open();
        var document = XDocument.Load(input);
        var root = document.Root ?? throw new InvalidOperationException("数据库结构快照为空。");
        var model = root.Element(root.Name.Namespace + "Model")
            ?? throw new InvalidOperationException("数据库结构快照缺少 Model 定义。");

        // 只去掉 SQL 登录的 Password，不忽略整个登录对象，更不触碰默认值、约束或表说明。
        foreach (var login in model.Elements().Where(element => (string?)element.Attribute("Type") == "SqlLogin"))
        {
            login.Elements().Where(element => element.Name.LocalName == "Property"
                && (string?)element.Attribute("Name") == "Password").Remove();
        }

        // Header 中的提取元信息不代表数据库结构；保留根属性（模型方言等）及全部 Model 定义。
        var normalized = new XElement(root.Name,
            root.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal),
            new XElement(model.Name, model.Attributes(), model.Elements()
                .OrderBy(element => (string?)element.Attribute("Type"), StringComparer.Ordinal)
                .ThenBy(element => (string?)element.Attribute("Name"), StringComparer.Ordinal)
                .ThenBy(element => element.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToString(SaveOptions.DisableFormatting))));
    }
}
