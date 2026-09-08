using DbStudio.Core;
using Microsoft.AspNetCore.Components;

namespace DbStudio.Components;

/// <summary>数据库工具的单表上下文；与整库模式共享连接、报告及执行确认。</summary>
public partial class DatabaseToolsPanel
{
    /// <summary>非空时锁定为单表模式；失效的 ID 不允许回退到整库。</summary>
    [Parameter] public string? TableId { get; set; }
    /// <summary>单表模式返回原设计页。</summary>
    [Parameter] public EventCallback ReturnRequested { get; set; }

    private bool SingleTable => TableId != null;
    private TableDesign? ScopeTable => Project.Tables.SingleOrDefault(table => table.Id == TableId);

    /// <summary>两种模式通过不同服务入口明确范围，计划生成之后由服务端固定执行包。</summary>
    private Task<DatabasePlan> CompareScopeAsync(CancellationToken token) => SingleTable
        ? Tools.CompareTableAsync(Principal, Project.Id, connectionId, TableId!, prune, allowDataLoss, token)
        : Tools.CompareAsync(Principal, Project.Id, connectionId, prune, allowDataLoss, token);
}
