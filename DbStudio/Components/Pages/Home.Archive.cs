using DbStudio.Core;

namespace DbStudio.Components.Pages;

public partial class Home
{
    /// <summary>打开已保存设计的归档表单，不隐式保存或丢弃当前编辑。</summary>
    private void OpenArchive()
    {
        if (project == null)
        {
            return;
        }
        Run(() =>
        {
            Store.RequireProject(principal, project.Id, ProjectAccess.Read | ProjectAccess.Export, Permission.Export);
            menu = "";
            modal = "archive";
        });
    }

    /// <summary>只在归档窗口仍打开时收起窗口，避免慢速下载关闭用户后来打开的其他窗口。</summary>
    private void ArchiveDownloaded()
    {
        if (modal == "archive")
        {
            modal = "";
        }
        message = "PDF 已生成，已交给浏览器下载。";
    }
}
