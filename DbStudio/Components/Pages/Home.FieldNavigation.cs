using DbStudio.Core;
using Microsoft.JSInterop;

namespace DbStudio.Components.Pages;

/// <summary>新字段渲染后定位；用稳定 ID 区分字段，避免改名或切表后聚焦错误的行。</summary>
public partial class Home
{
    private (string TableId, string ColumnId)? pendingColumnFocus;

    private void RevealColumn(ColumnDesign column)
    {
        if (table == null) { return; }
        fieldSearch = "";
        tab = "fields";
        pendingColumnFocus = (table.Id, column.Id);
    }

    private async Task FocusPendingColumnAsync()
    {
        var pending = pendingColumnFocus;
        pendingColumnFocus = null;
        if (pending == null || view != "design" || tab != "fields" || modal != ""
            || table?.Id != pending.Value.TableId || !table.Columns.Any(c => c.Id == pending.Value.ColumnId)) { return; }
        await JS.InvokeVoidAsync("studio.focusColumn", pending.Value.ColumnId);
    }
}
