using System.Security.Claims;
using DbStudio.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace DbStudio.Components.Pages;

/// <summary>
/// 工作台交互状态与页面操作；编辑草稿独立于服务端已保存文档。
/// </summary>
public partial class Home : IDisposable
{
    [Inject] StudioStore Store { get; set; } = default!;
    [Inject] AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] IJSRuntime JS { get; set; } = default!;
    [Inject] NavigationManager Navigation { get; set; } = default!;
    ClaimsPrincipal principal = new();
    SessionUser? user;
    List<DesignProject> projects = [];
    DesignProject? project;
    TableDesign? table;
    string search = "";
    string fieldSearch = "";
    string tab = "fields";
    string view = "overview";
    string menu = "";
    string modal = "";
    string message = "";
    string error = "";
    bool dirty;
    HashSet<string> collapsed = [];
    HashSet<string> selected = [];
    ColumnDesign? advanced;
    string projectName = "";
    string projectDescription = "";
    string sql = "";
    string oldPassword = "";
    string newPassword = "";
    List<Role> roles = []; List<UserInfo> users = []; List<AuditItem> audit = [];
    string editUserId = "";
    string editLogin = "";
    string editName = "";
    string editRole = "reader";
    string editPassword = "";
    bool editEnabled = true;
    string editRoleId = "";
    string editRoleName = "";
    Permission editPermissions = Permission.Read;
    DotNetObjectReference<Home>? reference;
    TaskCompletionSource<bool>? confirmation;
    string confirmationText = "";

    private string HelpTopic => view switch
    {
        "overview" => "start",
        "diagram" => "design",
        "versions" => "database",
        "database" or "table-database" => "database",
        "security" => "members",
        "design" when tab == "sql" => "backup",
        "design" => "design",
        _ => "start"
    };

    /// <summary>
    /// 从任意工作台页面打开说明，并优先定位到当前功能对应章节。
    /// </summary>
    private void OpenHelp()
    {
        menu = "";
        modal = "help";
    }

    /// <summary>
    /// 显示页面内确认窗口，等待用户选择后继续原操作。
    /// </summary>
    private Task<bool> Confirm(string text)
    {
        confirmationText = text;
        confirmation = new();
        StateHasChanged();
        return confirmation.Task;
    }

    /// <summary>
    /// 完成等待中的确认任务并清理弹窗状态。
    /// </summary>
    private void ResolveConfirm(bool result)
    {
        var pending = confirmation;
        confirmation = null;
        confirmationText = "";
        pending?.TrySetResult(result);
    }

    /// <summary>
    /// 用于界面可见性与禁用状态；实际授权仍由服务层执行。
    /// </summary>
    private bool Can(Permission permission) => user != null && (user.Permissions & permission) == permission;

    /// <summary>
    /// 读取会话和项目，建立与已保存文档隔离的编辑快照。
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        principal = (await Auth.GetAuthenticationStateAsync()).User;
        user = Store.Current(principal);
        if (user == null)
        {
            Navigation.NavigateTo("/login", true);
            return;
        }
        projects = Store.Projects(principal);
        project = projects.FirstOrDefault(p => p.Id == RequestedProjectId) ?? projects.FirstOrDefault();
        LoadProjectAccess();
        LoadProjectActivity();
        if (project != null)
        {
            foreach (var group in project.Tables.Select(t => t.Module).Distinct())
            {
                collapsed.Add(group);
            }
        }
    }

    /// <summary>
    /// 首次注册快捷键，每次渲染同步未保存提示和弹窗焦点。
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            reference = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("studio.init", reference);
        }
        await JS.InvokeVoidAsync("studio.setDirty", dirty);
        await JS.InvokeVoidAsync("studio.syncModal");
        await FocusPendingColumnAsync();
    }

    /// <summary>
    /// 释放传给 JavaScript 的页面引用。
    /// </summary>
    public void Dispose() => reference?.Dispose();
    IEnumerable<IGrouping<string, TableDesign>> Groups => (project?.Tables ?? []).Where(t => search == "" || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Columns.Any(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Label.Contains(search, StringComparison.OrdinalIgnoreCase))).GroupBy(t => t.Module);
    IEnumerable<ColumnDesign> VisibleColumns => (table?.Columns ?? []).Where(c => fieldSearch == "" || c.Name.Contains(fieldSearch, StringComparison.OrdinalIgnoreCase) || c.Label.Contains(fieldSearch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 记录编辑状态并清除过期成功提示。
    /// </summary>
    private void MarkDirty()
    {
        dirty = true;
        message = "";
    }

    /// <summary>
    /// 展开或收起指定业务模块。
    /// </summary>
    private void Toggle(string module)
    {
        if (!collapsed.Add(module))
        {
            collapsed.Remove(module);
        }
    }

    /// <summary>
    /// 更新单行选择状态。
    /// </summary>
    private void Select(string id, bool on)
    {
        if (on)
        {
            selected.Add(id);
        }
        else
        {
            selected.Remove(id);
        }
    }

    /// <summary>
    /// 对当前搜索可见的字段批量选择。
    /// </summary>
    private void SelectAll(bool on)
    {
        foreach (var c in VisibleColumns)
        {
            Select(c.Id, on);
        }
    }

    /// <summary>
    /// 存在未保存内容时要求用户明确选择是否放弃。
    /// </summary>
    private async Task<bool> Discard() => !databaseBusy && (!dirty || await Confirm("有未保存的设计修改，确定放弃这些修改吗？"));

    /// <summary>
    /// 确认放弃旧编辑后切换表，清空仅属于旧表的临时状态。
    /// </summary>
    private async Task ChooseTable(TableDesign item)
    {
        if (!await Discard())
        {
            return;
        }

        table = ModelJson.Clone(item);
        collapsed.Remove(item.Module);
        dirty = false;
        selected.Clear();
        advanced = null;
        fieldSearch = "";
        error = "";
        message = "";
        view = "design";
        if (tab == "sql")
        {
            GenerateSql();
        }
    }

    /// <summary>从顶部字段索引打开目标表，并使用稳定字段 ID 选中、滚动和聚焦对应行。</summary>
    private async Task OpenFieldSearchResult(ProjectFieldSearchEntry result)
    {
        var target = project?.Tables.FirstOrDefault(item => item.Id == result.TableId);
        if (target == null || !target.Columns.Any(column => column.Id == result.ColumnId) || !await Discard())
        {
            return;
        }

        table = ModelJson.Clone(target);
        collapsed.Remove(target.Module);
        dirty = false;
        selected.Clear();
        selected.Add(result.ColumnId);
        advanced = null;
        fieldSearch = "";
        tab = "fields";
        error = "";
        message = "";
        view = "design";
        pendingColumnFocus = (target.Id, result.ColumnId);
    }

    /// <summary>
    /// 只有用户接受放弃未保存内容后才切换项目。
    /// </summary>
    private async Task ChooseProject(string id)
    {
        if (!await Discard())
        {
            return;
        }

        project = projects.FirstOrDefault(p => p.Id == id);
        LoadProjectAccess();
        collapsed.Clear();
        search = "";
        ResetProjectHome();
        RememberProject();
    }

    /// <summary>
    /// 重新读取服务端快照并尽量保留当前表。
    /// </summary>
    private async Task Refresh()
    {
        if (!await Discard())
        {
            return;
        }

        Run(() =>
        {
            var id = project?.Id;
            var tid = table?.Id;
            projects = Store.Projects(principal);
            project = projects.FirstOrDefault(p => p.Id == id) ?? projects.FirstOrDefault();
            LoadProjectAccess();
            table = ModelJson.Clone(project?.Tables.FirstOrDefault(t => t.Id == tid));
            dirty = false;
            selected.Clear();
            advanced = null;
            if (view == "design" && table == null)
            {
                ResetProjectHome();
            }
            else if (view == "overview")
            {
                LoadProjectActivity();
            }
            if (tab == "sql" && table != null)
            {
                GenerateSql();
            }
        });
    }

    /// <summary>
    /// 统一展示可预期的业务错误，不让校验失败终止交互会话。
    /// </summary>
    private void Run(Action action)
    {
        error = "";
        message = "";
        try
        {
            action();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { error = "保存失败：账号或角色名称重复，或数据正被使用。请检查输入后重试。"; }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or FormatException) { error = ex.Message; }
    }

    /// <summary>
    /// 处理 Ctrl+S，仅在设计主页面且拥有维护权限时保存。
    /// </summary>
    [JSInvokable]
    public Task ShortcutSave()
    {
        if (view == "design" && modal == "" && CanDesign)
        {
            Save();
            StateHasChanged();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 优先取消确认窗口，否则关闭普通弹窗和菜单。
    /// </summary>
    [JSInvokable]
    public Task ShortcutEscape()
    {
        if (confirmation != null)
        {
            ResolveConfirm(false);
        }
        else
        {
            modal = "";
            menu = "";
        }
        StateHasChanged();
        return Task.CompletedTask;
    }

}
