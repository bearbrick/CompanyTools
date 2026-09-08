using DbStudio.Core;

namespace DbStudio.Components.Pages;

public partial class Home
{
    private ProjectAccess projectAccess;
    private bool databaseBusy;
    private string settingsSection = "info";

    /// <summary>项目切换后更新界面权限；业务入口始终独立重新授权。</summary>
    private void LoadProjectAccess() => projectAccess = project == null ? ProjectAccess.None : Store.AccessTo(principal, project.Id);

    private bool CanDesign => Can(Permission.Design) && projectAccess.HasFlag(ProjectAccess.Design);
    private bool CanExport => Can(Permission.Export) && projectAccess.HasFlag(ProjectAccess.Export);

    private async Task OpenDatabaseTools()
    {
        if (project == null || !await Discard())
        {
            return;
        }
        Run(() => Store.RequireProject(principal, project.Id, ProjectAccess.Database, Permission.Design));
        if (error != "")
        {
            return;
        }
        dirty = false;
        table = ModelJson.Clone(project.Tables.FirstOrDefault(t => t.Id == table?.Id) ?? project.Tables.FirstOrDefault());
        view = "database";
        menu = "";
    }

    /// <summary>项目设置使用已保存文档，打开前处理未保存表草稿。</summary>
    private async Task OpenProjectSettings(string section = "info")
    {
        if (project == null || !await Discard())
        {
            return;
        }
        table = ModelJson.Clone(project.Tables.FirstOrDefault(t => t.Id == table?.Id) ?? project.Tables.FirstOrDefault());
        dirty = false;
        settingsSection = section;
        modal = "settings";
        menu = "";
    }

    /// <summary>保存项目或模块后同步版本并刷新当前表的模块归属。</summary>
    private void ProjectSettingsSaved(DesignProject updated)
    {
        project = updated;
        projects[projects.FindIndex(p => p.Id == updated.Id)] = updated;
        table = ModelJson.Clone(updated.Tables.FirstOrDefault(t => t.Id == table?.Id) ?? updated.Tables.FirstOrDefault());
        LoadProjectAccess();
    }

    /// <summary>包含独立空模块的表树分组；搜索时隐藏无匹配项的空组。</summary>
    private IEnumerable<(string Key, List<TableDesign> Tables)> ModuleGroups
    {
        get
        {
            if (project == null)
            {
                yield break;
            }
            foreach (var module in ModuleOrdering.Names(project))
            {
                var tables = project.Tables.Where(t => t.Module == module && (search == "" || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Columns.Any(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Label.Contains(search, StringComparison.OrdinalIgnoreCase)))).ToList();
                if (search == "" || tables.Count > 0)
                {
                    yield return (module, tables);
                }
            }
        }
    }
}
