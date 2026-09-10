using DbStudio.Core;

namespace DbStudio.Components.Pages;

/// <summary>字段批量移动交互，与字段编辑、复制等操作分开维护。</summary>
public partial class Home
{
    /// <summary>无编辑权限、无有效选择或所有选中行均无法移动时禁用按钮。</summary>
    private bool CanMoveColumns(int direction) => CanDesign && table != null
        && ColumnOrdering.CanMove(table.Columns, selected, direction);

    /// <summary>按完整字段顺序移动草稿，保留勾选和字段属性，仅在实际移动后标记待保存。</summary>
    private void MoveColumns(int direction)
    {
        if (CanDesign && table != null && ColumnOrdering.Move(table.Columns, selected, direction))
        {
            MarkDirty();
        }
    }
}
