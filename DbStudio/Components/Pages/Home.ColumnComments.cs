using DbStudio.Core;

namespace DbStudio.Components.Pages;

/// <summary>备注使用独立编辑缓冲区，取消或关闭窗口不会修改表草稿。</summary>
public partial class Home
{
    private ColumnDesign? commentColumn;
    private string? commentTableId;
    private string commentDraft = "";

    /// <summary>复制当前备注到弹窗草稿；仅允许编辑当前表中的字段。</summary>
    private void OpenColumnComment(ColumnDesign column)
    {
        if (!CanDesign || table == null || !table.Columns.Contains(column))
        {
            return;
        }
        commentColumn = column;
        commentTableId = table.Id;
        commentDraft = column.Comment;
        modal = "column-comment";
    }

    /// <summary>将确认后的备注应用到表草稿，由保存设计统一持久化。</summary>
    private void ApplyColumnComment()
    {
        if (!CanDesign || table == null || table.Id != commentTableId || commentColumn == null || !table.Columns.Contains(commentColumn))
        {
            return;
        }
        if (commentColumn.Comment != commentDraft)
        {
            commentColumn.Comment = commentDraft;
            MarkDirty();
            message = "备注已更新，请保存设计。";
        }
        modal = "";
    }
}
