using DbStudio.Core;
using Microsoft.JSInterop;

namespace DbStudio.Components.Pages;

/// <summary>跨表字段复制粘贴，只改目标草稿；复制本身不会把来源表标记为未保存。</summary>
public partial class Home
{
    private string columnClipboardText = "";
    private bool columnClipboardBusy;

    /// <summary>经设计及导出授权后冻结选中字段，写入系统剪贴板；复制不改变来源草稿。</summary>
    private async Task CopyColumns()
    {
        if (!CanDesign || !CanExport || project == null || table == null || selected.Count == 0 || columnClipboardBusy) { return; }
        string text = "";
        var clipboard = new ColumnClipboard();
        Run(() =>
        {
            Store.RequireProject(principal, project.Id, ProjectAccess.Design | ProjectAccess.Export, Permission.Design | Permission.Export);
            clipboard.Capture(table.Columns, selected);
            text = clipboard.Serialize();
        });
        if (error != "") { return; }
        columnClipboardBusy = true;
        try
        {
            await JS.InvokeVoidAsync("studio.copy", text);
            message = $"已将 {clipboard.Count} 个字段复制到剪贴板，请到目标表点击「批量粘贴」。";
        }
        catch (JSException) { error = "浏览器未能写入剪贴板，请重新点击「批量复制」。"; }
        finally { columnClipboardBusy = false; }
    }

    /// <summary>读取系统剪贴板；HTTP 或浏览器拒绝读取时提供 Ctrl+V 输入框，不使用旧缓存。</summary>
    private async Task OpenColumnPaste()
    {
        if (!CanDesign || project == null || table == null || columnClipboardBusy) { return; }
        var target = table;
        columnClipboardBusy = true;
        error = message = columnClipboardText = "";
        try
        {
            var text = await JS.InvokeAsync<string?>("studio.readColumnClipboard", ColumnClipboard.MaxCharacters);
            if (!ReferenceEquals(table, target) || view != "design") { return; }
            columnClipboardText = text ?? "";
            if (text == null) { modal = "paste-columns"; }
            else { PasteColumns(); }
        }
        catch (JSException) { error = "剪贴板内容过大或读取失败，请减少复制的字段后重试。"; }
        finally { columnClipboardBusy = false; }
    }

    /// <summary>验证目标权限及全部剪贴板内容后一次性生成草稿，保存时继续核验类型和约束。</summary>
    private void PasteColumns()
    {
        if (!CanDesign || project == null || table == null) { return; }
        Run(() =>
        {
            Store.RequireProject(principal, project.Id, ProjectAccess.Design, Permission.Design);
            var clipboard = ColumnClipboard.Parse(columnClipboardText);
            var copies = clipboard.CreateCopies(table.Columns);
            table.Columns.AddRange(copies);
            selected.Clear();
            selected.UnionWith(copies.Select(column => column.Id));
            fieldSearch = "";
            advanced = null;
            modal = "";
            columnClipboardText = "";
            MarkDirty();
            RevealColumn(copies[0]);
            message = $"已粘贴 {copies.Count} 个字段到表末尾，请核对并保存。主键、自增需在目标表设置；重名字段已自动改名。"
                + (copies.Any(column => column.Computed != "") ? "计算列表达式已保留，请核对引用的字段。" : "");
        });
    }
}
