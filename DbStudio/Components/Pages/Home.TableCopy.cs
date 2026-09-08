using DbStudio.Core;
using Microsoft.Data.Sqlite;

namespace DbStudio.Components.Pages;

/// <summary>复制表工作流：冻结来源、独立编辑新表信息，成功保存后再切换当前表。</summary>
public partial class Home
{
    private TableDesign? tableCopySource;
    private TableCopyOptions? tableCopyOptions;
    private string tableCopyError = "";

    /// <summary>准备独立表单，不改变当前草稿、选中字段或未保存状态。</summary>
    private void OpenTableCopy()
    {
        if (project == null || table == null || !CanDesign)
        {
            return;
        }
        Run(() =>
        {
            Store.RequireProject(principal, project.Id, ProjectAccess.Design, Permission.Design);
            tableCopySource = ModelJson.Clone(table);
            var suggested = DesignEditing.CopyTable(project, tableCopySource);
            tableCopyOptions = new TableCopyOptions
            {
                Name = suggested.Name,
                Schema = suggested.Schema,
                Label = suggested.Label,
                Module = suggested.Module,
                Comment = suggested.Comment
            };
            tableCopyError = "";
            modal = "copy-table";
        });
    }

    /// <summary>
    /// 按最终表名生成独立结构，使用现有保存事务验证权限、重名及项目修订号。
    /// 失败时保留原表与复制表单；成功后展开新表模块并打开字段定义。
    /// </summary>
    private void CreateTableCopy()
    {
        if (modal != "copy-table" || project == null || tableCopySource == null || tableCopyOptions == null)
        {
            return;
        }
        tableCopyError = "";
        try
        {
            var copy = DesignEditing.CopyTable(project, tableCopySource, tableCopyOptions);
            var saved = Store.SaveTable(principal, project.Id, project.Revision, copy);
            project = saved;
            projects[projects.FindIndex(item => item.Id == saved.Id)] = saved;
            table = ModelJson.Clone(saved.Tables.Single(item => item.Id == copy.Id));
            selected.Clear();
            collapsed.Remove(table.Module);
            search = "";
            fieldSearch = "";
            advanced = null;
            tab = "fields";
            view = "design";
            dirty = false;
            error = "";
            modal = "";
            tableCopySource = null;
            tableCopyOptions = null;
            message = $"已复制并创建 {table.Schema}.{table.Name} · {table.Columns.Count} 个字段 · r{saved.Revision}";
        }
        catch (SqliteException)
        {
            tableCopyError = "创建失败：设计数据正被使用，请稍后重试。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            tableCopyError = ex.Message;
        }
    }
}
