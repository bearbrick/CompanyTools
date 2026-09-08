using DbStudio.Core;
using Microsoft.AspNetCore.Components;

namespace DbStudio.Components;

/// <summary>表级约束编辑面板，独立管理紧凑布局，复用工作台的草稿与保存流程。</summary>
public partial class ConstraintPanel
{
    /// <summary>当前表的编辑草稿。</summary>
    [Parameter, EditorRequired] public TableDesign Table { get; set; } = default!;
    /// <summary>当前项目，用于显示外键目标。</summary>
    [Parameter, EditorRequired] public DesignProject Project { get; set; } = default!;
    /// <summary>当前面板类型：indexes、foreign 或 checks。</summary>
    [Parameter] public string Kind { get; set; } = "indexes";
    /// <summary>是否允许修改草稿。</summary>
    [Parameter] public bool CanEdit { get; set; }
    /// <summary>外键引用表选项。</summary>
    [Parameter] public List<SelectOption> TableOptions { get; set; } = [];
    /// <summary>当前表的字段选项。</summary>
    [Parameter] public List<SelectOption> LocalColumnOptions { get; set; } = [];
    /// <summary>级联更新、删除规则选项。</summary>
    [Parameter] public List<SelectOption> ActionOptions { get; set; } = [];
    /// <summary>通知工作台标记草稿已修改。</summary>
    [Parameter] public EventCallback Changed { get; set; }
    /// <summary>通过工作台既有逻辑新增对应约束。</summary>
    [Parameter] public EventCallback AddRequested { get; set; }

    /// <summary>字段双向绑定后通知父页面，保持保存状态一致。</summary>
    private Task NotifyChangedAsync() => Changed.InvokeAsync();

    /// <summary>仅从草稿移除指定索引，实际持久化由保存设计完成。</summary>
    private async Task RemoveIndexAsync(IndexDesign index)
    {
        if (!CanEdit) { return; }
        Table.Indexes.Remove(index);
        await NotifyChangedAsync();
    }

    /// <summary>仅从草稿移除指定外键。</summary>
    private async Task RemoveForeignKeyAsync(ForeignKeyDesign key)
    {
        if (!CanEdit) { return; }
        Table.ForeignKeys.Remove(key);
        await NotifyChangedAsync();
    }

    /// <summary>仅从草稿移除指定检查约束。</summary>
    private async Task RemoveCheckAsync(CheckDesign check)
    {
        if (!CanEdit) { return; }
        Table.Checks.Remove(check);
        await NotifyChangedAsync();
    }
}
