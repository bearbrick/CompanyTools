using System.IO.Compression;
using System.Xml.Linq;
using DbStudio.Core;

/// <summary>覆盖真实提取中登录密码占位值变化，确保放行非结构差异而仍拒绝真实定义变更。</summary>
internal static class SchemaModelFingerprintChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var project = new DesignProject { Tables = [new() { Name = "FingerprintTable", Label = "说明", Columns = [
            new() { Name = "Id", Type = "int", Default = "1" }, new() { Name = "Value", Type = "nvarchar", Length = "20" }] }] };
        var package = SqlServerTools.BuildPackage(project);
        var first = WithLogin(package, "generated-placeholder-one", false);
        var second = WithLogin(package, "generated-placeholder-two", true);
        check("Fingerprint ignores regenerated login password and top-level enumeration order", SchemaModelFingerprint.Create(first) == SchemaModelFingerprint.Create(second));
        var baseline = SchemaModelFingerprint.Create(first);
        project.Tables[0].Columns[1].Length = "40";
        check("Fingerprint detects column length changes", baseline != SchemaModelFingerprint.Create(WithLogin(SqlServerTools.BuildPackage(project), "generated-placeholder-two", false)));
        project.Tables[0].Columns[1].Length = "20";
        project.Tables[0].Columns[0].Default = "2";
        check("Fingerprint detects default expression changes", baseline != SchemaModelFingerprint.Create(WithLogin(SqlServerTools.BuildPackage(project), "generated-placeholder-two", false)));
        project.Tables[0].Columns[0].Default = "1";
        project.Tables[0].Label = "更新说明";
        check("Fingerprint detects descriptions", baseline != SchemaModelFingerprint.Create(WithLogin(SqlServerTools.BuildPackage(project), "generated-placeholder-two", false)));
        check("Fingerprint retains non-password login properties", baseline != SchemaModelFingerprint.Create(WithLogin(package, "generated-placeholder-one", false, "otherdb")));
    }

    /// <summary>在有效 DacFx 模型上模拟导出的 SQL 登录，测试只读取 XML，不创建服务器登录。</summary>
    private static byte[] WithLogin(byte[] package, string password, bool reverseOrder, string database = "master")
    {
        using var input = new MemoryStream(package);
        using var original = new ZipArchive(input);
        using var modelInput = original.GetEntry("model.xml")!.Open();
        var document = XDocument.Load(modelInput);
        var ns = document.Root!.Name.Namespace;
        var model = document.Root.Element(ns + "Model")!;
        model.Add(new XElement(ns + "Element", new XAttribute("Type", "SqlLogin"), new XAttribute("Name", "[FixtureLogin]"),
            new XElement(ns + "Property", new XAttribute("Name", "Password"), new XAttribute("Value", password)),
            new XElement(ns + "Property", new XAttribute("Name", "DefaultDatabase"), new XAttribute("Value", database))));
        if (reverseOrder) { var elements = model.Elements().Reverse().ToList(); model.ReplaceNodes(elements); }
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using var writer = archive.CreateEntry("model.xml").Open();
            document.Save(writer);
        }
        return output.ToArray();
    }
}
