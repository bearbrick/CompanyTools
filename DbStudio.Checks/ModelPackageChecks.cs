using System.IO.Compression;
using System.Xml.Linq;
using DbStudio.Core;

/// <summary>比较包必须通过 DacFx 的模型解析，普通 T-SQL 语法解析不足以覆盖批处理限制。</summary>
internal static class ModelPackageChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var project = new DesignProject
        {
            Tables =
            [
                new()
                {
                    Name = "DescriptionSample", Label = "表说明",
                    Columns =
                    [
                        new() { Name = "Id", Type = "int", Label = "主键说明", Nullable = false, PrimaryKeyOrder = 1 },
                        new() { Name = "Name", Type = "nvarchar", Length = "40", Label = "名称说明", Comment = "引号 ' 与换行\nGO\n仍属于备注" }
                    ]
                }
            ]
        };
        byte[] package;
        try
        {
            package = SqlServerTools.BuildPackage(project);
        }
        catch (Microsoft.SqlServer.Dac.Model.DacModelException ex)
        {
            // 部分 DacFx 异常的 ToString() 依赖内部模型，单独输出 Message 保留诊断。
            throw new InvalidOperationException("含表和字段说明的模型构建失败：" + ex.Message);
        }
        using var stream = new MemoryStream(package);
        using var archive = new ZipArchive(stream);
        using var modelStream = archive.GetEntry("model.xml")!.Open();
        var model = XDocument.Load(modelStream);
        var descriptions = model.Descendants().Where(e => e.Name.LocalName == "Element" && (string?)e.Attribute("Type") == "SqlExtendedProperty").ToList();
        check("DacFx accepts table and multiple column descriptions", descriptions.Count == 3);
        check("DacFx retains quotes and GO inside description literals", model.ToString().Contains("名称说明") && model.ToString().Contains("仍属于备注") && model.ToString().Contains("主键说明"));
    }
}
