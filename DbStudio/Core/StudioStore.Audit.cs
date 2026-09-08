using System.Security.Claims;

namespace DbStudio.Core;

/// <summary>
/// 审计查询。记录与业务写入共享连接及事务，查询按时间顺序展示最近操作。
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>
    /// 返回最近一百条审计记录，时间为 ISO 8601 格式的 UTC 时间。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    public List<AuditItem> Audit(ClaimsPrincipal principal, string? projectId = null)
    {
        var actor = Require(principal, Permission.Read);
        if (projectId == null)
        {
            Require(principal, Permission.Security);
            if (actor.RoleId != "admin")
            {
                throw new UnauthorizedAccessException("全局历史审计仅系统管理员可查看，请查看所属项目的操作记录。");
            }
        }
        else
        {
            RequireProject(principal, projectId, ProjectAccess.Read);
        }
        using var db = Open();
        using var cmd = Command(db, """
            SELECT Time,Actor,Action,Detail FROM Audit a
            WHERE ($all=1 OR ProjectId=$project)
            AND ($admin=1 OR ($all=1 AND ProjectId='') OR EXISTS (
                SELECT 1 FROM ProjectMembers m WHERE m.ProjectId=a.ProjectId AND m.UserId=$user AND (m.Access & 1)=1
            )) ORDER BY Id DESC LIMIT 100
            """, ("$all", projectId == null ? 1 : 0), ("$project", projectId ?? ""),
            ("$admin", actor.RoleId == "admin" ? 1 : 0), ("$user", actor.Id));
        using var rows = cmd.ExecuteReader();
        var result = new List<AuditItem>();
        while (rows.Read())
        {
            result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3)));
        }

        return result;
    }
}
