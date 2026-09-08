using Microsoft.JSInterop;

namespace DbStudio.Components.Pages;

public partial class Home
{
    /// <summary>顶部导出项目全部已保存结构；单表编辑区的 SQL 预览和下载保持独立。</summary>
    private async Task DownloadProjectSql()
    {
        menu = "";
        if (project == null) { return; }
        var fileName = project.Name + ".sql";
        string content = "";
        Run(() => content = Store.ExportProjectSql(principal, project.Id));
        if (content == "") { return; }
        await JS.InvokeVoidAsync("studio.download", fileName, content, "text/plain;charset=utf-8");
        message = dirty ? "项目 SQL 已生成，仅包含已保存内容；当前未保存修改未导出。" : "项目全部已保存表的 SQL 已生成，已交给浏览器下载。";
    }
}
