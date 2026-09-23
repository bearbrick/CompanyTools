using System.Security.Claims;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    /// <summary>保存项目级技术字段名单；只改变 ER 图的显示，不修改表结构或实际外键。</summary>
    public DesignProject SaveTechnicalFields(ClaimsPrincipal principal, string projectId, int revision, IEnumerable<string> fieldNames)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Design, Permission.Design);
            var names = fieldNames.Select(name => name.Trim()).Where(name => name != "").ToList();
            if (names.Count > 100 || names.Any(name => name.Length > 128))
            {
                throw new InvalidOperationException("技术字段最多 100 个，每个字段名最多 128 字符。");
            }
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            {
                throw new InvalidOperationException("技术字段名不能重复（不区分大小写）。");
            }
            using var db = Open();
            using var tx = db.BeginTransaction();
            var project = ReadProject(db, projectId);
            project.TechnicalFields = names;
            if (CommitProject(db, project, revision, new(actor.DisplayName, "design", "设置技术字段", project.Name)))
            {
                Log(db, actor.DisplayName, "设置技术字段", $"{project.Name} / {names.Count} 个字段名", projectId);
            }
            tx.Commit();
            return project;
        }
    }
}
