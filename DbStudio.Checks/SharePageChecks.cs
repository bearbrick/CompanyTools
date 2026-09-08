using System.Net;
using DbStudio.Components.Pages;
using DbStudio.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>直接渲染匿名页，验证真实输出的结构信息、跳转和 HTML 编码。</summary>
internal static class SharePageChecks
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var parent = new TableDesign { Id = "parent", Name = "Parent", Label = "父表" };
        var child = new TableDesign
        {
            Id = "child",
            Name = "Child",
            Label = "子表",
            PrimaryKeyName = "PK_Custom_Child",
            PrimaryKeyClustered = false,
            PrimaryKeyDescendingColumns = "Id",
            Columns =
            [
                new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                new() { Name = "Name", Type = "nvarchar", Length = "80", Collation = "Latin1_General_100_CI_AS", Comment = "<script>alert(1)</script>" },
                new() { Name = "Total", Type = "int", Computed = "[Id] * 2", Persisted = true }
            ],
            Indexes = [new() { Name = "IX_Child_Name", Columns = "Name,Id", DescendingColumns = "Name" }],
            ForeignKeys = [new() { Name = "FK_Child_Parent", Columns = "Id", TargetTableId = parent.Id, TargetColumns = "Id" }]
        };
        var project = new DesignProject { Name = "匿名评审", Tables = [parent, child] };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        async Task<string> Render(string view, string table = "child", string search = "Child") =>
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = await renderer.RenderComponentAsync<SharedProjectPage>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SharedProjectPage.Project)] = project,
                    [nameof(SharedProjectPage.Token)] = new string('a', 64),
                    [nameof(SharedProjectPage.View)] = view,
                    [nameof(SharedProjectPage.TableId)] = table,
                    [nameof(SharedProjectPage.Search)] = search
                }));
                return result.ToHtmlString();
            });

        var rawTable = await Render("table");
        var tableHtml = WebUtility.HtmlDecode(rawTable);
        var cardsHtml = WebUtility.HtmlDecode(await Render("cards"));
        check("Share renders custom primary key and direction", tableHtml.Contains("PK_Custom_Child") && tableHtml.Contains("[Id] DESC") && tableHtml.Contains("非聚集"));
        check("Both share views retain index order and collation", new[] { tableHtml, cardsHtml }.All(html => html.Contains("[Name] DESC, [Id] ASC") && html.Contains("Latin1_General_100_CI_AS")));
        check("Share cards retain persisted computation", cardsHtml.Contains("[Id] * 2") && cardsHtml.Contains("[持久化]"));
        check("Share foreign key clears search while view switch retains it", tableHtml.Contains("?table=parent&view=table&q=\"") && tableHtml.Contains("?table=child&view=cards&q=Child"));
        var parentHtml = WebUtility.HtmlDecode(await Render("table", "parent", ""));
        check("Share foreign key destination displays referenced table", parentHtml.Contains("<h2>父表</h2>"));
        check("Share encodes untrusted comments and has no editing runtime", rawTable.Contains("&lt;script&gt;") && !rawTable.Contains("<script") && !rawTable.Contains("_framework/blazor") && !rawTable.Contains("<textarea"));
    }
}
