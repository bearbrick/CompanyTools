using DbStudio.Core;
using Microsoft.AspNetCore.Components;

namespace DbStudio.Components;

/// <summary>复制表的信息表单；仅编辑副本信息，通过回调交给页面提交设计。</summary>
public partial class TableCopyForm
{
    /// <summary>打开窗口时冻结的源表，供展示复制范围。</summary>
    [Parameter, EditorRequired] public TableDesign Source { get; set; } = default!;

    /// <summary>独立的新表基本信息。</summary>
    [Parameter, EditorRequired] public TableCopyOptions Options { get; set; } = default!;

    /// <summary>当前项目的可选模块，允许输入新模块。</summary>
    [Parameter] public List<SelectOption> Modules { get; set; } = [];

    /// <summary>提示副本包含尚未写回原表的编辑。</summary>
    [Parameter] public bool HasUnsavedChanges { get; set; }

    /// <summary>服务端校验或保存失败的信息，保留表单供修正。</summary>
    [Parameter] public string Error { get; set; } = "";

    /// <summary>用户提交后由父页面完成权限检查及保存。</summary>
    [Parameter] public EventCallback Create { get; set; }

    /// <summary>关闭表单，放弃副本信息。</summary>
    [Parameter] public EventCallback Cancel { get; set; }

    private bool submitting;

    /// <summary>等待一次创建操作完成，避免用户连续提交同一个表单。</summary>
    private async Task SubmitAsync()
    {
        if (submitting)
        {
            return;
        }
        submitting = true;
        try
        {
            await Create.InvokeAsync();
        }
        finally
        {
            submitting = false;
        }
    }
}
