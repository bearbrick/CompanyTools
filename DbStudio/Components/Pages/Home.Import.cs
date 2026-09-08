using DbStudio.Core;
using Microsoft.AspNetCore.Components.Forms;

namespace DbStudio.Components.Pages;

public partial class Home
{
    private string importJson = "";
    private string importName = "";
    private bool importReading;
    private int importReadVersion;
    private ProjectImportPreview? importPreview;

    /// <summary>初始化备份恢复向导；先处理当前未保存草稿，避免提交导入后意外丢失编辑。</summary>
    private async Task OpenImport()
    {
        if (!await Discard())
        {
            return;
        }

        menu = "";
        importReadVersion++;
        importReading = false;
        importJson = importName = "";
        importPreview = null;
        error = "";
        modal = "import";
    }

    /// <summary>读取单个受限大小的 JSON 文件；版本号避免较早的异步读取覆盖新选择。</summary>
    private async Task ReadImportFile(InputFileChangeEventArgs args)
    {
        var version = ++importReadVersion;
        importReading = true;
        importPreview = null;
        importJson = "";
        error = "";
        try
        {
            var file = args.File;
            await using var stream = file.OpenReadStream(ProjectBackup.MaxBytes);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            if (version != importReadVersion || modal != "import")
            {
                return;
            }

            importJson = content;
            PreviewImport();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            if (version == importReadVersion)
            {
                error = "文件读取失败，请选择不超过 10 MB 的 JSON 文件。";
            }
        }
        finally
        {
            if (version == importReadVersion)
            {
                importReading = false;
            }
        }
    }

    /// <summary>编辑文本后立即使旧预览失效，防止显示旧清单却导入新内容。</summary>
    private void InvalidateImport()
    {
        importPreview = null;
    }

    /// <summary>生成不持久化的预览；有结构错误时允许阅读清单，但不允许提交。</summary>
    private void PreviewImport()
    {
        importPreview = null;
        Run(() =>
        {
            importPreview = Store.PreviewImport(principal, importJson);
            var sourceName = importPreview.Project.Name;
            importName = sourceName[..Math.Min(sourceName.Length, 90)] + "（导入）";
        });
    }

    /// <summary>创建独立项目，成功后切换工作台；服务失败时保留导入内容供修正。</summary>
    private void RestoreProject()
    {
        if (importReading || importPreview == null || importPreview.Errors.Count != 0)
        {
            return;
        }

        Run(() =>
        {
            project = Store.ImportProject(principal, importJson, importName);
            projects.Add(project);
            LoadProjectAccess();
            table = ModelJson.Clone(project.Tables.FirstOrDefault());
            selected.Clear();
            collapsed.Clear();
            search = fieldSearch = "";
            advanced = null;
            dirty = false;
            tab = "fields";
            view = "design";
            modal = "";
            importPreview = null;
            importJson = "";
            message = $"已导入新项目「{project.Name}」，共 {project.Tables.Count} 张表。";
        });
    }
}
