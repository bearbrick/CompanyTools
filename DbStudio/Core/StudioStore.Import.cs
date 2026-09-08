using System.Security.Claims;
using System.Text.Json;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    /// <summary>导入预览也要求项目创建和设计维护权限，不写入数据库。</summary>
    /// <param name="principal">调用者当前会话。</param>
    /// <param name="json">导入文档。</param>
    public ProjectImportPreview PreviewImport(ClaimsPrincipal principal, string json)
    {
        Require(principal, Permission.Projects | Permission.Design);
        return ProjectBackup.Inspect(json);
    }

    /// <summary>
    /// 将备份恢复为独立项目。提交时重新授权、解析和校验，不信任预览结果。
    /// 项目、表和字段生成新 ID，所有外键统一重映射，项目文档和审计原子提交。
    /// </summary>
    /// <param name="principal">调用者当前会话。</param>
    /// <param name="json">原始备份文本。</param>
    /// <param name="name">新项目名称。</param>
    /// <returns>版本为 1 的新项目。</returns>
    public DesignProject ImportProject(ClaimsPrincipal principal, string json, string name)
    {
        lock (gate)
        {
            var actor = Require(principal, Permission.Projects | Permission.Design);
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            {
                throw new InvalidOperationException("新项目名称必填且不能超过 100 字符。");
            }

            var preview = ProjectBackup.Inspect(json);
            if (preview.Errors.Count != 0)
            {
                throw new InvalidOperationException("请先修复备份中的结构问题，再重新预览导入。");
            }

            var project = preview.Project;
            var oldName = project.Name;
            project.Id = Guid.NewGuid().ToString("N");
            project.Name = name.Trim();
            project.Revision = 1;
            var tableIds = project.Tables.ToDictionary(t => t.Id, _ => Guid.NewGuid().ToString("N"));
            foreach (var table in project.Tables)
            {
                table.Id = tableIds[table.Id];
                foreach (var column in table.Columns)
                {
                    column.Id = Guid.NewGuid().ToString("N");
                }

                foreach (var foreignKey in table.ForeignKeys)
                {
                    foreignKey.TargetTableId = tableIds[foreignKey.TargetTableId];
                }
            }

            using var db = Open();
            using var transaction = db.BeginTransaction();
            Execute(db, "INSERT INTO Projects VALUES($id,$name,1,$doc)",
                ("$id", project.Id), ("$name", project.Name), ("$doc", JsonSerializer.Serialize(project, ModelJson.Options)));
            AddProjectOwner(db, project.Id, actor.Id);
            Log(db, actor.DisplayName, "导入项目备份", $"{oldName} → {project.Name} · {project.Tables.Count} 张表", project.Id);
            transaction.Commit();
            return project;
        }
    }
}
