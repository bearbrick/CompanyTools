using System.Security.Claims;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    /// <summary>重新校验项目导出权限，从保存快照生成全部表的 SQL，不采用浏览器当前草稿或搜索结果。</summary>
    public string ExportProjectSql(ClaimsPrincipal principal, string projectId)
    {
        RequireProject(principal, projectId, ProjectAccess.Export, Permission.Export);
        using var db = Open();
        return SqlServerDdl.GenerateProject(ReadProject(db, projectId));
    }
}
