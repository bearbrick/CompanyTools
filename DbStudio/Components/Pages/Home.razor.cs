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
            if (view == "design" && table == null) { ResetProjectHome(); }
            else if (view == "overview") { LoadProjectActivity(); }
            if (tab == "sql" && table != null) { GenerateSql(); }
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

    /// <summary>
    /// 提交当前快照与修订号，成功后使用服务端返回的版本更新界面。
    /// </summary>
    private void Save() => Run(() =>
    {
        if (project == null || table == null)
        {
            return;
        }

        var previousRevision = project.Revision;
        designIssues = DesignValidation.Check(WorkingProject(), table);
        if (designIssues.Count > 0)
        {
            modal = "validation";
            error = "设计检查未通过，请修正后再保存。";
            return;
        }
        project = Store.SaveTable(principal, project.Id, previousRevision, table);
        projects[projects.FindIndex(p => p.Id == project.Id)] = project;
        table = ModelJson.Clone(project.Tables.First(t => t.Id == table.Id));
        dirty = false;
        message = project.Revision == previousRevision ? "内容未变化，无需保存 · r" + project.Revision : "设计已保存 · r" + project.Revision;
    });

    /// <summary>
    /// 新建带默认主键的编辑草稿，保存前不写数据库。
    /// </summary>
    private async Task NewTable()
    {
        if (!CanDesign || !await Discard())
        {
            return;
        }

        table = new TableDesign { Name = "NewTable" + ((project?.Tables.Count ?? 0) + 1), Module = table?.Module ?? "未分组", Columns = [new ColumnDesign { Name = "Id", Label = "主键", Type = "bigint", Nullable = false, PrimaryKeyOrder = 1, Identity = true }] };
        dirty = true;
        tab = "fields";
        view = "design";
        selected.Clear();
    }

    /// <summary>
    /// 确认后删除设计表；引用完整性由服务层校验。
    /// </summary>
    private async Task DeleteTable()
    {
        menu = "";
        if (project == null || table == null || !await Confirm($"确定删除设计表 {table.Name}？此操作只删除设计，不操作实际数据库。"))
        {
            return;
        }

        Run(() => { project = Store.SaveTable(principal, project.Id, project.Revision, table, true); projects[projects.FindIndex(p => p.Id == project.Id)] = project; table = ModelJson.Clone(project.Tables.FirstOrDefault()); dirty = false; message = "设计表已删除"; });
    }

    /// <summary>
    /// 追加具有不重复默认名称的字段。
    /// </summary>
    private void AddColumn()
    {
        if (table == null || !CanDesign)
        {
            return;
        }

        var n = table.Columns.Count + 1;
        while (table.Columns.Any(c => c.Name == "Column" + n))
        {
            n++;
        }

        var column = new ColumnDesign { Name = "Column" + n };
        table.Columns.Add(column);
        selected.Clear();
        selected.Add(column.Id);
        RevealColumn(column);
        MarkDirty();
        message = $"已新增字段 {column.Name}，请填写字段名和定义名。";
    }

    /// <summary>
    /// 确认后删除选中字段，关联约束在保存时统一验证。
    /// </summary>
    private async Task DeleteColumns()
    {
        if (table == null || selected.Count == 0 || !await Confirm($"删除已选择的 {selected.Count} 个字段？关联约束需要一并调整。"))
        {
            return;
        }

        table.Columns.RemoveAll(c => selected.Contains(c.Id));
        selected.Clear();
        advanced = null;
        MarkDirty();
    }

    /// <summary>
    /// 设置复合主键顺序，加入主键时同步设为非空。
    /// </summary>
    private void SetPk(ColumnDesign c, bool on)
    {
        c.PrimaryKeyOrder = on ? (table?.Columns.Max(x => x.PrimaryKeyOrder) ?? 0) + 1 : 0;
        if (on)
        {
            c.Nullable = false;
        }

        MarkDirty();
    }

    /// <summary>
    /// 更新字段空值规则并标记未保存。
    /// </summary>
    private void SetNullable(ColumnDesign c, bool nullable)
    {
        c.Nullable = nullable;
        MarkDirty();
    }

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
    /// 确认旧编辑后创建项目，并切换到新项目。
    /// </summary>
    private async Task CreateProject()
    {
        if (!await Discard())
        {
            return;
        }

        Run(() => { project = Store.NewProject(principal, projectName, projectDescription); projects.Add(project); LoadProjectAccess(); ResetProjectHome(); RememberProject(); });
    }

    /// <summary>
    /// 切换设计面板，按需生成 SQL 或读取审计。
    /// </summary>
    private void OpenTab(string value)
    {
        tab = value;
        error = "";
        if (value == "sql")
        {
            GenerateSql();
        }

        if (value == "history")
        {
            Run(() => audit = Store.Audit(principal, project?.Id));
        }
    }

    /// <summary>
    /// 合成包含当前草稿的项目快照，供跨表约束校验与 SQL 生成。
    /// </summary>
    private DesignProject WorkingProject()
    {
        var copy = ModelJson.Clone(project!);
        var i = copy.Tables.FindIndex(t => t.Id == table!.Id);
        if (i >= 0)
        {
            copy.Tables[i] = table!;
        }
        else
        {
            copy.Tables.Add(table!);
        }

        return copy;
    }

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
        string content = "";
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
        catch (JSException) { error = "浏览器未允许访问剪贴板，请选中 SQL 手动复制。"; }
    }

    /// <summary>
    /// 根据当前面板分派新增索引、外键或检查约束。
    /// </summary>
    private void AddConstraint()
    {
        if (tab == "indexes")
        {
            AddIndex();
        }
        else if (tab == "foreign")
        {
            AddForeignKey();
        }
        else
        {
            AddCheck();
        }
    }

    /// <summary>
    /// 先关闭表信息窗口，再进入删除确认流程。
    /// </summary>
    private async Task DeleteFromModal()
    {
        modal = "";
        await DeleteTable();
    }

    /// <summary>
    /// 添加索引草稿。
    /// </summary>
    private void AddIndex()
    {
        table!.Indexes.Add(new IndexDesign { Name = "IX_" + table.Name + "_" + (table.Indexes.Count + 1) });
        MarkDirty();
    }

    /// <summary>
    /// 添加外键草稿，引用表和字段由用户填写。
    /// </summary>
    private void AddForeignKey()
    {
        table!.ForeignKeys.Add(new ForeignKeyDesign { Name = "FK_" + table.Name + "_" + (table.ForeignKeys.Count + 1) });
        MarkDirty();
    }

    /// <summary>
    /// 添加检查约束草稿。
    /// </summary>
    private void AddCheck()
    {
        table!.Checks.Add(new CheckDesign { Name = "CK_" + table.Name + "_" + (table.Checks.Count + 1) });
        MarkDirty();
    }

    /// <summary>
    /// 读取最新成员与角色后进入权限管理页面。
    /// </summary>
    private void Security()
    {
        menu = "";
        Run(() => { roles = Store.Roles(principal); users = Store.Users(principal); view = "security"; });
    }

    /// <summary>
    /// 初始化新增或编辑成员表单，不读取已有密码。
    /// </summary>
    private void EditUser(UserInfo? item = null)
    {
        editUserId = item?.Id ?? Guid.NewGuid().ToString("N");
        editLogin = item?.Login ?? "";
        editName = item?.DisplayName ?? "";
        editRole = item?.RoleId ?? "reader";
        editEnabled = item?.Enabled ?? true;
        editPassword = "";
        modal = "user";
    }

    /// <summary>
    /// 保存成员并刷新列表，旧会话失效规则由服务层处理。
    /// </summary>
    private void SaveUser() => Run(() => { Store.SaveUser(principal, new(editUserId, editLogin, editName, editRole, editEnabled), editPassword); users = Store.Users(principal); modal = ""; message = "用户已保存；已有会话需重新登录。"; });

    /// <summary>
    /// 初始化角色权限编辑表单。
    /// </summary>
    private void EditRole(Role? item = null)
    {
        editRoleId = item?.Id ?? Guid.NewGuid().ToString("N");
        editRoleName = item?.Name ?? "";
        editPermissions = item?.Permissions ?? Permission.Read;
        modal = "role";
    }

    /// <summary>
    /// 增减角色的单个权限位。
    /// </summary>
    private void SetPermission(Permission permission, bool on)
    {
        if (on)
        {
            editPermissions |= permission;
        }
        else
        {
            editPermissions &= ~permission;
        }
    }

    /// <summary>
    /// 保存角色并刷新权限列表。
    /// </summary>
    private void SaveRole() => Run(() => { Store.SaveRole(principal, new(editRoleId, editRoleName, editPermissions)); roles = Store.Roles(principal); modal = ""; message = "角色权限已更新，后续操作立即按新权限检查。"; });

    /// <summary>
    /// 修改密码成功后回到登录页，要求建立新会话。
    /// </summary>
    private void ChangePassword() => Run(() => { Store.ChangePassword(principal, oldPassword, newPassword); dirty = false; Navigation.NavigateTo("/login", true); });

    /// <summary>
    /// 将权限位转换为统一的中文名称。
    /// </summary>
    private static string PermissionName(Permission p) => p switch { Permission.Read => "查看设计", Permission.Design => "维护设计", Permission.Projects => "创建项目", Permission.Export => "导出与 SQL", Permission.Security => "用户与角色", _ => "" };
    static readonly Permission[] PermissionList = [Permission.Read, Permission.Design, Permission.Projects, Permission.Export, Permission.Security];
}
