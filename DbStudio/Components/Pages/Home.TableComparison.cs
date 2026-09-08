using DbStudio.Core;

namespace DbStudio.Components.Pages;

/// <summary>从当前表进入单表比对，先保存当前草稿，再绑定服务端设计版本。</summary>
public partial class Home
{
    private string? tableComparisonId;
    private bool CanCompareTable => Can(Permission.Design) && projectAccess.HasFlag(ProjectAccess.Database);

    /// <summary>保存失败时留在设计页；成功后仅打开当前表的比对上下文。</summary>
    private void OpenTableComparison()
    {
        if (project == null || table == null || !CanCompareTable || databaseBusy) { return; }
        if (dirty)
        {
            Save();
            if (dirty || error != "") { return; }
        }
        Run(() =>
        {
            Store.RequireProject(principal, project.Id, ProjectAccess.Database, Permission.Design);
            if (!project.Tables.Any(item => item.Id == table.Id))
            {
                throw new InvalidOperationException("请先保存当前表设计。");
            }
            tableComparisonId = table.Id;
            view = "table-database";
            menu = "";
        });
    }

    /// <summary>保留当前表及设计选项卡，返回继续编辑。</summary>
    private void ReturnFromTableComparison()
    {
        if (databaseBusy) { return; }
        view = "design";
        tableComparisonId = null;
    }
}
