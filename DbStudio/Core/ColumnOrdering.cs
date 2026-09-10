namespace DbStudio.Core;

/// <summary>字段批量排序：相邻选中行作为一组移动，保持字段对象及选中行之间的相对顺序。</summary>
public static class ColumnOrdering
{
    /// <summary>只要有一组选中行能跨过相邻的未选中行，就允许向指定方向移动。</summary>
    public static bool CanMove(IReadOnlyList<ColumnDesign> columns, IReadOnlySet<string> selected, int direction)
    {
        if (direction is not (-1 or 1) || selected.Count == 0) { return false; }
        for (var index = 0; index < columns.Count; index++)
        {
            var neighbor = index + direction;
            if (neighbor >= 0 && neighbor < columns.Count
                && selected.Contains(columns[index].Id) && !selected.Contains(columns[neighbor].Id))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 上移从前往后处理，下移从后往前处理，确保每个可移动的选中行恰好移动一格。
    /// 已到边界的分组保持原位，其他分组仍可移动；没有实际变化时返回 false。
    /// </summary>
    public static bool Move(List<ColumnDesign> columns, IReadOnlySet<string> selected, int direction)
    {
        if (!CanMove(columns, selected, direction)) { return false; }
        var start = direction == -1 ? 1 : columns.Count - 2;
        var step = -direction;
        for (var index = start; index >= 0 && index < columns.Count; index += step)
        {
            var neighbor = index + direction;
            if (selected.Contains(columns[index].Id) && !selected.Contains(columns[neighbor].Id))
            {
                (columns[index], columns[neighbor]) = (columns[neighbor], columns[index]);
            }
        }
        return true;
    }
}
