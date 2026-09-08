using System.Security.Claims;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    /// <summary>授权后读取已保存归档快照；拒绝过期版本，不接收浏览器传入的表结构，也不保存编辑草稿。</summary>
    public StructureArchive ReadStructureArchive(ClaimsPrincipal principal, string projectId, int revision, StructureArchiveOptions options)
    {
        RequireProject(principal, projectId, ProjectAccess.Read | ProjectAccess.Export, Permission.Export);
        options.Validate();
        using var db = Open();
        var project = ReadProject(db, projectId);
        if (project.Revision != revision)
        {
            throw new InvalidOperationException("项目已更新，请刷新项目后重新导出 PDF；本次未导出旧版本。");
        }
        if (project.Tables.Count == 0)
        {
            throw new InvalidOperationException("项目尚无已保存的数据表，请先保存表结构再导出。");
        }
        return new StructureArchive(project, ModelJson.Clone(options), DateTimeOffset.Now);
    }
}
