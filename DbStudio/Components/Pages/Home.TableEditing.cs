using DbStudio.Core;

namespace DbStudio.Components.Pages;

/// <summary>
/// 数据表和字段草稿的创建、保存与删除操作。
/// </summary>
public partial class Home
{
    /// <summary>
    /// 提交当前快照与修订号，成功后使用服务端返回的版本更新界面。
    /// </summary>
    private void Save() => Run(() =>
    {
        if (project == null || table == null)
        {
            return;
        }

        var previousRevision = project.Revision;
        designIssues = DesignValidation.Check(WorkingProject(), table);
        if (designIssues.Count > 0)
        {
            modal = "validation";
            error = "设计检查未通过，请修正后再保存。";
            return;
        }
        project = Store.SaveTable(principal, project.Id, previousRevision, table);
        projects[projects.FindIndex(candidate => candidate.Id == project.Id)] = project;
        table = ModelJson.Clone(project.Tables.First(candidate => candidate.Id == table.Id));
        if (tab == "diagram")
        {
            relationshipPreview = WorkingProject();
        }
        dirty = false;
        message = project.Revision == previousRevision
            ? "内容未变化，无需保存 · r" + project.Revision
            : "设计已保存 · r" + project.Revision;
    });

    /// <summary>
    /// 新建带默认主键的编辑草稿，保存前不写数据库。
    /// </summary>
    private async Task NewTable()
    {
        if (!CanDesign || !await Discard())
        {
            return;
        }

        table = new TableDesign
        {
            Name = "NewTable" + ((project?.Tables.Count ?? 0) + 1),
            Module = table?.Module ?? "未分组",
            Columns = [new ColumnDesign
            {
                Name = "Id",
                Label = "主键",
                Type = "bigint",
                Nullable = false,
                PrimaryKeyOrder = 1,
                Identity = true
            }]
        };
        dirty = true;
        tab = "fields";
        view = "design";
        selected.Clear();
    }

    /// <summary>
    /// 确认后删除设计表；引用完整性由服务层校验。
    /// </summary>
    private async Task DeleteTable()
    {
        menu = "";
        if (project == null || table == null
            || !await Confirm($"确定删除设计表 {table.Name}？此操作只删除设计，不操作实际数据库。"))
        {
            return;
        }

        Run(() =>
        {
            project = Store.SaveTable(principal, project.Id, project.Revision, table, true);
            projects[projects.FindIndex(candidate => candidate.Id == project.Id)] = project;
            table = ModelJson.Clone(project.Tables.FirstOrDefault());
            dirty = false;
            message = "设计表已删除";
        });
    }

    /// <summary>
    /// 追加具有不重复默认名称的字段。
    /// </summary>
    private void AddColumn()
    {
        if (table == null || !CanDesign)
        {
            return;
        }

        var suffix = table.Columns.Count + 1;
        while (table.Columns.Any(column => column.Name == "Column" + suffix))
        {
            suffix++;
        }

        var column = new ColumnDesign { Name = "Column" + suffix };
        table.Columns.Add(column);
        selected.Clear();
        selected.Add(column.Id);
        RevealColumn(column);
        MarkDirty();
        message = $"已新增字段 {column.Name}，请填写字段名和定义名。";
    }

    /// <summary>
    /// 确认后删除选中字段，关联约束在保存时统一验证。
    /// </summary>
    private async Task DeleteColumns()
    {
        if (table == null || selected.Count == 0
            || !await Confirm($"删除已选择的 {selected.Count} 个字段？关联约束需要一并调整。"))
        {
            return;
        }

        table.Columns.RemoveAll(column => selected.Contains(column.Id));
        selected.Clear();
        advanced = null;
        MarkDirty();
    }

    /// <summary>
    /// 设置复合主键顺序，加入主键时同步设为非空。
    /// </summary>
    private void SetPk(ColumnDesign column, bool enabled)
    {
        column.PrimaryKeyOrder = enabled ? (table?.Columns.Max(candidate => candidate.PrimaryKeyOrder) ?? 0) + 1 : 0;
        if (enabled)
        {
            column.Nullable = false;
        }
        MarkDirty();
    }

    /// <summary>
    /// 更新字段空值规则并标记未保存。
    /// </summary>
    private void SetNullable(ColumnDesign column, bool nullable)
    {
        column.Nullable = nullable;
        MarkDirty();
    }
}
