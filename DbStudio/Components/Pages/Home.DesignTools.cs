using DbStudio.Core;

namespace DbStudio.Components.Pages;

public partial class Home
{
    private List<string> designIssues = [];

    /// <summary>
    /// 检查当前草稿和入站外键，不保存数据，也不要求 SQL 导出权限。
    /// 每次打开重新计算，避免展示旧的检查结果。
    /// </summary>
    private void CheckDesign()
    {
        if (project == null || table == null)
        {
            return;
        }

        Run(() =>
        {
            Store.RequireProject(principal, project.Id, ProjectAccess.Read);
            designIssues = SqlServerDdl.ValidateChange(WorkingProject(), table);
            modal = "validation";
        });
    }

}
