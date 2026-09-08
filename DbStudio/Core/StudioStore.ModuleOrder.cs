using System.Security.Claims;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    /// <summary>
    /// 保存完整模块排列，只调整导航元数据。校验权限、成员集合和项目版本，
    /// 防止旧窗口覆盖新模块；相同顺序不增加修订号或审计记录。
    /// </summary>
    /// <param name="principal">当前操作者，必须拥有项目设计权限。</param>
    /// <param name="projectId">需要排序的项目。</param>
    /// <param name="revision">客户端读取的项目修订号。</param>
    /// <param name="names">包含所有模块且不重复的完整排列。</param>
    public DesignProject SaveModuleOrder(ClaimsPrincipal principal, string projectId, int revision, IReadOnlyList<string> names)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Design, Permission.Design);
            using var db = Open();
            using var tx = db.BeginTransaction();
            var project = ReadProject(db, projectId);
            var current = ModuleOrdering.Names(project);
            if (names.Count != current.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Count
                || !current.ToHashSet(StringComparer.Ordinal).SetEquals(names))
            {
                throw new InvalidOperationException("模块清单已变化或排序包含重复项，请刷新后重试。");
            }
            // 隐式模块的显示次序未变时，不因补全 Modules 数组制造一次保存。
            if (!current.SequenceEqual(names, StringComparer.Ordinal))
            {
                project.Modules = names.ToList();
            }
            if (CommitProject(db, project, revision))
            {
                Log(db, actor.DisplayName, "调整模块顺序", $"{project.Name} · {names.Count} 个模块", projectId);
            }
            tx.Commit();
            return project;
        }
    }
}
