using System.IO.Compression;
using System.Xml.Linq;
using DbStudio.Core;
using Microsoft.SqlServer.Dac;

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
                    Name = "DescriptionSample", Label = "表说明", Comment = "仅在 Studio 显示的表备注",
                    Columns =
                    [
                        new() { Name = "Id", Type = "int", Label = "主键说明", Nullable = false, PrimaryKeyOrder = 1 },
                        new() { Name = "Name", Type = "nvarchar", Length = "40", Label = "名称 ' 与换行\nGO\n仍属于定义名", Comment = "仅在 Studio 显示的字段备注" },
                        new() { Name = "Unlabelled", Type = "int", Label = "", Comment = "不能用备注冒充定义名" }
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
        check("DacFx retains quotes and GO inside definition names", model.ToString().Contains("仍属于定义名") && model.ToString().Contains("主键说明"));
        check("DacFx excludes table notes column notes and notes-only fields", !model.ToString().Contains("仅在 Studio") && !model.ToString().Contains("不能用备注冒充"));

        using var originalStream = new MemoryStream(package);
        using var original = DacPackage.Load(originalStream);
        int DifferenceCount(DesignProject changed)
        {
            using var changedStream = new MemoryStream(SqlServerTools.BuildPackage(changed));
            using var changedPackage = DacPackage.Load(changedStream);
            var report = XDocument.Parse(DacServices.GenerateDeployReport(changedPackage, original, "DescriptionCheck", new DacDeployOptions()));
            return report.Descendants().Where(e => e.Name.LocalName == "Operation").SelectMany(e => e.Descendants().Where(i => i.Name.LocalName == "Item")).Count();
        }
        var notesOnly = ModelJson.Clone(project);
        notesOnly.Tables[0].Comment = "表备注修改";
        notesOnly.Tables[0].Columns[1].Comment = "字段备注修改";
        check("Editing Studio notes produces no database changes", DifferenceCount(notesOnly) == 0);
        var labelChanged = ModelJson.Clone(project);
        labelChanged.Tables[0].Columns[1].Label = "新的定义名";
        check("Editing a definition name produces one database annotation change", DifferenceCount(labelChanged) == 1);
    }
}
