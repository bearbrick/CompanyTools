using System.Xml.Linq;
using DbStudio.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.Dac;

/// <summary>使用真实 DacFx 离线比对报告验证警告，不连接或修改业务库。</summary>
internal static class DeployReportChecks
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var targetProject = new DesignProject
        {
            Tables = [new() { Name = "WarningSample", Columns = [new() { Name = "Id", Type = "int", Nullable = false }, new() { Name = "Name", Type = "nvarchar", Length = "80" }] }]
        };
        var sourceProject = ModelJson.Clone(targetProject);
        sourceProject.Tables[0].Columns[0].PrimaryKeyOrder = 1;
        sourceProject.Tables[0].Columns[1].Length = "20";
        using var sourceStream = new MemoryStream(SqlServerTools.BuildPackage(sourceProject));
        using var targetStream = new MemoryStream(SqlServerTools.BuildPackage(targetProject));
        using var source = DacPackage.Load(sourceStream);
        using var target = DacPackage.Load(targetStream);
        var report = DacServices.GenerateDeployReport(source, target, "WarningSampleDb", new DacDeployOptions { BlockOnPossibleDataLoss = true });
        var warnings = DeploymentWarnings.Parse(XDocument.Parse(report));
        check("Actual DacFx clustered index warning retains index and table", warnings.Single(w => w.Code == "CreateClusteredIndex").Issues.Any(i => i.Message.Contains("PK_WarningSample") && i.Message.Contains("[dbo].[WarningSample]")));
        var dataIssue = warnings.Single(w => w.Code == "DataIssue");
        check("Actual DacFx data warning retains column and old/new types", dataIssue.Issues.Any(i => i.Message.Contains("Name") && i.Message.Contains("80") && i.Message.Contains("20")));
        check("DacFx issue ids associate affected operation objects", dataIssue.Issues.Any(i => i.Id != "" && i.Objects.Any(o => o.Name == "[dbo].[WarningSample]" && o.Type == "SqlTable")));
        check("DacFx warnings expose Chinese titles and original XML", dataIssue.Title == "数据兼容性风险" && dataIssue.RawXml.Contains("Issue") && dataIssue.RawXml.Contains("Value="));

        var variants = DeploymentWarnings.Parse(XDocument.Parse("""
            <DeploymentReport><Alerts>
                <Alert Name="FutureWarning"><Issue Value="未知诊断 &lt;script&gt;unsafe&lt;/script&gt;"><Item Value="[dbo].[Other]" Type="SqlTable" /></Issue></Alert>
                <Alert Name="TextWarning"><Issue>文本式原因</Issue><Issue Message="属性式原因" /></Alert>
                <Alert Name="EmptyWarning" />
            </Alerts></DeploymentReport>
            """));
        check("Unknown warning retains source message and embedded object", variants[0].Title == "其他部署提示" && variants[0].Issues[0].Message.Contains("<script>") && variants[0].Issues[0].Objects.Single().Name == "[dbo].[Other]");
        check("Text and Message attribute diagnostics remain visible", variants[1].Issues.Select(i => i.Message).SequenceEqual(["文本式原因", "属性式原因"]));
        check("Missing diagnostic explains limitation without inventing a reason", variants[2].Issues.Single().Message.StartsWith("报告未提供具体说明"));
        await RenderChecks(warnings, variants, check);
    }

    /// <summary>渲染真实展示组件，确认原文被编码，且具体风险无需展开 XML 即可阅读。</summary>
    private static async Task RenderChecks(List<DeploymentWarning> warnings, List<DeploymentWarning> variants, Action<string, bool> check)
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        async Task<string> Render(List<DeploymentWarning> items) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DbStudio.Components.DatabaseWarnings>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?> { ["Items"] = items }));
            return component.ToHtmlString();
        });
        var rendered = await Render(warnings);
        var text = System.Net.WebUtility.HtmlDecode(rendered);
        check("Warning UI shows concrete causes objects and expandable raw XML", text.Contains("数据兼容性风险") && text.Contains("[dbo].[WarningSample]") && text.Contains("NVARCHAR (80)") && text.Contains("NVARCHAR (20)") && rendered.Contains("<details"));
        var untrusted = await Render(variants);
        check("Warning UI encodes diagnostic text and XML", !untrusted.Contains("<script>") && untrusted.Contains("&lt;script&gt;") && !untrusted.Contains("<Alert"));
        check("Warning UI stays absent for report without alerts", string.IsNullOrWhiteSpace(await Render([])));
        var directory = Path.Combine(Path.GetTempPath(), "DbStudio-warning-preview");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "warnings.html"), "<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>部署警告验证</title><link rel='stylesheet' href='app.css'><link rel='stylesheet' href='workspace.css'><body><main class='database-tools'><section class='tools-result'><h2>结构差异</h2>" + rendered + "</section></main></body></html>");
        Console.WriteLine("WARNING UI FIXTURE: " + directory);
    }
}
