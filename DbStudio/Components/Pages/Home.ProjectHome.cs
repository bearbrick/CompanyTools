using DbStudio.Core;
using Microsoft.AspNetCore.Components;

namespace DbStudio.Components.Pages;

/// <summary>项目首页导航和浏览器刷新上下文，不依赖特定业务表名称。</summary>
public partial class Home
{
    /// <summary>从地址查询参数读取并恢复的项目标识。</summary>
    [SupplyParameterFromQuery(Name = "project")]
    public string? RequestedProjectId
    {
        get; set;
    }
    private IReadOnlyList<AuditItem> projectActivity = [];

    /// <summary>只在进入或刷新首页时读取当前项目日志，先清空旧内容，避免跨项目显示。</summary>
    private void LoadProjectActivity()
    {
        projectActivity = [];
        if (project != null)
        {
            projectActivity = Store.Audit(principal, project.Id).Take(20).ToList();
        }
    }

    /// <summary>离开草稿前沿用放弃确认；同步执行期间禁止切换。</summary>
    private async Task OpenProjectHome()
    {
        if (!await Discard())
        {
            return;
        }
        ResetProjectHome();
    }

    /// <summary>放弃未保存草稿后打开当前项目的已保存 ER 关系图。</summary>
    private async Task OpenRelationshipDiagram()
    {
        if (!await Discard() || project == null)
        {
            return;
        }
        table = null;
        dirty = false;
        selected.Clear();
        advanced = null;
        fieldSearch = "";
        menu = modal = message = error = "";
        view = "diagram";
    }

    /// <summary>放弃未保存草稿后打开项目级修订、发布和数据库版本状态。</summary>
    private async Task OpenVersions()
    {
        if (!await Discard() || project == null)
        {
            return;
        }
        table = null;
        dirty = false;
        selected.Clear();
        advanced = null;
        fieldSearch = "";
        menu = modal = message = error = "";
        view = "versions";
    }

    /// <summary>恢复成功后使用服务端返回的新修订刷新工作台。</summary>
    private Task VersionRestored(DesignProject restored)
    {
        project = restored;
        var index = projects.FindIndex(item => item.Id == restored.Id);
        if (index >= 0)
        {
            projects[index] = restored;
        }

        if (table != null)
        {
            table = ModelJson.Clone(restored.Tables.FirstOrDefault(item => item.Id == table.Id));
        }

        dirty = false;
        selected.Clear();
        advanced = null;
        LoadProjectActivity();
        return Task.CompletedTask;
    }

    /// <summary>清空表级编辑状态，使首页统计始终来自已保存的项目文档。</summary>
    private void ResetProjectHome()
    {
        table = null;
        dirty = false;
        selected.Clear();
        advanced = null;
        tableComparisonId = null;
        fieldSearch = "";
        tab = "fields";
        view = "overview";
        menu = modal = message = error = "";
        LoadProjectActivity();
    }

    /// <summary>项目标识放入地址栏，浏览器刷新后仍进入当前项目首页；不增加无用的历史记录。</summary>
    private void RememberProject() => Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("project", project?.Id), replace: true);
}
