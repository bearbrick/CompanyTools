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

    /// <summary>
    /// 复制当前编辑内容作为新表草稿。有未保存修改时先说明副本的来源并请求确认，
    /// 原表的已保存内容保持不变；副本仍需用户点击保存设计才持久化。
    /// </summary>
    private async Task DuplicateTable()
    {
        if (project == null || table == null || !CanDesign)
        {
            return;
        }
        if (dirty && !await Confirm("当前表有未保存修改。将把这些修改带入副本，原表保留已保存版本，确定继续吗？"))
        {
            return;
        }

        Run(() =>
        {
            Store.Require(principal, Permission.Design);
            table = DesignEditing.CopyTable(project, table);
            selected.Clear();
            fieldSearch = "";
            advanced = null;
            tab = "fields";
            view = "design";
            modal = "table";
            MarkDirty();
            message = "已创建完整表结构副本，调整表名后保存。";
        });
    }
}
