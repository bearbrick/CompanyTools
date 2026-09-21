using System.Net;
using System.Text.RegularExpressions;
using DbStudio.Components;
using DbStudio.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>直接渲染首页，核对统计口径、空项目、只读入口及用户文本编码。</summary>
internal static class ProjectOverviewChecks
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(DesignProject project, bool canDesign, IReadOnlyList<AuditItem>? activities = null) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<ProjectOverview>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ProjectOverview.Project)] = project, [nameof(ProjectOverview.CanDesign)] = canDesign,
                [nameof(ProjectOverview.Activities)] = activities ?? []
            }));
            return result.ToHtmlString();
        });
        var project = new DesignProject
        {
            Name = "<script>首页</script>", Modules = ["空模块", "业务"],
            Tables = [new() { Name = "A", Module = "业务", Columns = [new() { PrimaryKeyOrder = 1 }, new()], Indexes = [new()], ForeignKeys = [new()], Checks = [new()] },
                new() { Name = "B", Module = "业务", Columns = [new()] }]
        };
        var html = await Render(project, true);
        var counts = Regex.Matches(html, @"<strong\b[^>]*>([\d,]+)</strong>").Select(m => m.Groups[1].Value);
        check("Overview counts saved tables columns empty modules and constraints without duplicating PK", counts.SequenceEqual(["2", "3", "2", "1", "1", "1"]));
        check("Overview safely encodes project names", html.Contains("&lt;script&gt;") && !html.Contains("<script>"));
        check("Overview offers design entry only to designers", WebUtility.HtmlDecode(html).Contains("新建表") && !WebUtility.HtmlDecode(await Render(project, false)).Contains("新建表"));
        var empty = WebUtility.HtmlDecode(await Render(new(), true));
        check("Empty project overview has zero metrics and activity guidance", Regex.Matches(empty, @"<strong\b[^>]*>0</strong>").Count == 6 && empty.Contains("暂无项目活动"));
        var activityHtml = await Render(project, false, [new("2026-09-10T09:00:00Z", "设计成员", "保存表", "dbo.Customer · <script>说明</script>")]);
        var activityText = WebUtility.HtmlDecode(activityHtml);
        check("Overview shows real activity actor action time and detail", activityText.Contains("设计成员") && activityText.Contains("保存表") && activityText.Contains("2026-09-10") && activityText.Contains("dbo.Customer"));
        check("Overview replaces duplicated table search and encodes activity detail", !activityText.Contains("首页查找数据表") && !activityText.Contains("数据表快捷入口") && !activityHtml.Contains("<script>"));
    }
}
