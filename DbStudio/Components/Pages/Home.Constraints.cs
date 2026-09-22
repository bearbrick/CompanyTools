using DbStudio.Core;

namespace DbStudio.Components.Pages;

/// <summary>
/// 数据表索引、关系和检查约束的草稿操作。
/// </summary>
public partial class Home
{
    /// <summary>
    /// 根据当前面板分派新增索引、表关系或检查约束。
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
    /// 添加逻辑关系草稿，可由用户在关系面板切换为物理外键。
    /// </summary>
    private void AddForeignKey()
    {
        table!.ForeignKeys.Add(new ForeignKeyDesign
        {
            Name = "FK_" + table.Name + "_" + (table.ForeignKeys.Count + 1),
            IsLogical = true
        });
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
}
