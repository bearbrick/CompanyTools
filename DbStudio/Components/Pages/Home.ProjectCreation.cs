using DbStudio.Core;

namespace DbStudio.Components.Pages;

/// <summary>
/// 项目创建、设计面板切换和工作快照组装。
/// </summary>
public partial class Home
{
    /// <summary>
    /// 初始化创建项目表单。
    /// </summary>
    private void OpenNewProject()
    {
        menu = "";
        projectName = "";
        projectDescription = "";
        modal = "project";
    }

    /// <summary>
    /// 确认放弃旧草稿后创建项目，并切换到新项目。
    /// </summary>
    private async Task CreateProject()
    {
        if (!await Discard())
        {
            return;
        }

        Run(() =>
        {
            project = Store.NewProject(principal, projectName, projectDescription);
            projects.Add(project);
            LoadProjectAccess();
            ResetProjectHome();
            RememberProject();
        });
    }

    /// <summary>
    /// 切换设计面板，进入 SQL 面板时即时生成预览。
    /// </summary>
    private void OpenTab(string value)
    {
        tab = value;
        error = "";
        if (value == "sql")
        {
            GenerateSql();
        }
    }

    /// <summary>
    /// 合成包含当前草稿的项目快照，供跨表约束校验与 SQL 生成。
    /// </summary>
    private DesignProject WorkingProject()
    {
        var copy = ModelJson.Clone(project!);
        var index = copy.Tables.FindIndex(candidate => candidate.Id == table!.Id);
        if (index >= 0)
        {
            copy.Tables[index] = table!;
        }
        else
        {
            copy.Tables.Add(table!);
        }
        return copy;
    }
}
