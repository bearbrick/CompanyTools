using Microsoft.JSInterop;

namespace DbStudio.Components.Pages;

/// <summary>
/// 当前表 SQL 预览、复制与导出操作。
/// </summary>
public partial class Home
{
    /// <summary>
    /// 生成当前草稿 SQL，结构错误统一显示。
    /// </summary>
    private void GenerateSql()
    {
        sql = "";
        Run(() => sql = Store.Ddl(principal, WorkingProject(), table!));
    }

    /// <summary>
    /// 生成成功后通过浏览器下载 SQL 文本。
    /// </summary>
    private async Task DownloadSql()
    {
        GenerateSql();
        if (sql != "")
        {
            await JS.InvokeVoidAsync("studio.download", table!.Name + ".sql", sql);
        }
    }

    /// <summary>
    /// 导出服务端已保存项目快照，不隐式保存草稿。
    /// </summary>
    private async Task ExportJson()
    {
        menu = "";
        var content = "";
        Run(() => content = Store.ExportProject(principal, project!.Id));
        if (content != "")
        {
            await JS.InvokeVoidAsync("studio.download", project!.Name + ".json", content, "application/json;charset=utf-8");
        }
    }

    /// <summary>
    /// 复制预览文本，剪贴板被浏览器拒绝时显示可操作的提示。
    /// </summary>
    private async Task CopySql()
    {
        try
        {
            await JS.InvokeVoidAsync("studio.copy", sql);
            message = "SQL 已复制";
        }
        catch (JSException)
        {
            error = "浏览器未允许访问剪贴板，请选中 SQL 手动复制。";
        }
    }
}
