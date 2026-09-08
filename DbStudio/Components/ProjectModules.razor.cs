using System.Security.Claims;
using DbStudio.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace DbStudio.Components;

/// <summary>模块列表维护：持久化成功后更新顺序，失败保留当前列表及编辑信息。</summary>
public partial class ProjectModules : IAsyncDisposable
{
    [Inject] private StudioStore Store { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>当前项目的已保存快照。</summary>
    [Parameter, EditorRequired] public DesignProject Project { get; set; } = default!;
    /// <summary>调用方身份，服务层会再次检查权限。</summary>
    [Parameter, EditorRequired] public ClaimsPrincipal Principal { get; set; } = default!;
    /// <summary>界面是否允许维护模块。</summary>
    [Parameter] public bool CanEdit { get; set; }
    /// <summary>将最新项目和修订号传回工作台。</summary>
    [Parameter] public EventCallback<DesignProject> Saved { get; set; }

    private List<string> OrderedModules { get; set; } = [];
    private string? oldName;
    private string moduleName = "", problem = "", notice = "";
    private bool busy;
    private ElementReference listElement;
    private IJSObjectReference? dragModule;
    private DotNetObjectReference<ProjectModules>? reference;

    /// <summary>父页面收到新版本后，以服务端顺序重新生成列表。</summary>
    protected override void OnParametersSet() => OrderedModules = ModuleOrdering.Names(Project);

    /// <summary>拖动反馈在浏览器内处理，只在放下时提交一次服务端操作，避免网络往返打断拖动。</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            dragModule = await JS.InvokeAsync<IJSObjectReference>("import", "./module-order.js");
            reference = DotNetObjectReference.Create(this);
            await dragModule.InvokeVoidAsync("attach", listElement, reference);
        }
    }

    /// <summary>处理拖动结果；模块成员、权限和位置检查仍通过正常保存路径执行。</summary>
    [JSInvokable]
    public async Task ReorderModule(string source, string target)
    {
        await MoveAsync(source, OrderedModules.IndexOf(target));
        // JS 回调不会像 Blazor 按钮事件一样自动触发最终渲染。
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>关闭模块面板后释放事件监听及回调引用，断线时无需继续发送浏览器调用。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (dragModule != null)
            {
                await dragModule.InvokeVoidAsync("detach", listElement);
                await dragModule.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        finally { reference?.Dispose(); }
    }

    /// <summary>构造完整的新排列，权限与并发检查通过后才替换已保存快照。</summary>
    private async Task MoveAsync(string module, int position)
    {
        var current = OrderedModules.IndexOf(module);
        if (!CanEdit || busy || current < 0 || position < 0 || position >= OrderedModules.Count || current == position)
        {
            return;
        }
        var next = OrderedModules.ToList();
        next.RemoveAt(current);
        next.Insert(position, module);
        await SaveAsync(() => Store.SaveModuleOrder(Principal, Project.Id, Project.Revision, next), "模块顺序已保存");
    }

    /// <summary>准备重命名，位置由服务端原位保留。</summary>
    private void BeginRename(string module) => (oldName, moduleName) = (module, module);

    /// <summary>放弃名称草稿，切回创建模块。</summary>
    private void ClearName() => (oldName, moduleName) = (null, "");

    /// <summary>新增模块追加末尾；重命名不改变已有次序。</summary>
    private async Task SaveNameAsync()
    {
        if (await SaveAsync(() => Store.SaveModule(Principal, Project.Id, Project.Revision, oldName, moduleName), "模块已保存"))
        {
            ClearName();
        }
    }

    /// <summary>串行提交一次用户操作，错误在模块面板中展示，避免未成功的排序留在界面。</summary>
    private async Task<bool> SaveAsync(Func<DesignProject> save, string success)
    {
        if (!CanEdit || busy) { return false; }
        busy = true;
        problem = notice = "";
        try
        {
            var updated = save();
            await Saved.InvokeAsync(updated);
            OrderedModules = ModuleOrdering.Names(updated);
            notice = success;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
        catch (SqliteException)
        {
            problem = "保存失败，设计数据正被使用，请稍后重试。";
            return false;
        }
        finally { busy = false; }
    }
}
