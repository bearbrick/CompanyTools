using DbStudio.Core;
using Microsoft.AspNetCore.Antiforgery;

namespace DbStudio.Web;

/// <summary>
/// 项目 HTTP API，复用与 Blazor 完全相同的服务授权和保存校验。
/// </summary>
internal static class ProjectEndpoints
{
    /// <summary>
    /// 映射会话信息、项目查询及表定义写入；写入必须携带防伪令牌。
    /// </summary>
    internal static void MapStudioProjects(this WebApplication app)
    {
        app.MapGet(
            "/api/session",
            (HttpContext ctx, IAntiforgery csrf, StudioStore store) => Results.Ok(new { user = store.Require(ctx.User, Permission.Read), csrf = csrf.GetAndStoreTokens(ctx).RequestToken })).RequireAuthorization();
        app.MapGet("/api/projects", (HttpContext ctx, StudioStore store) => store.Projects(ctx.User)).RequireAuthorization();
        app.MapPost(
            "/api/projects/{id}/archive.pdf",
            async (string id, HttpContext ctx, IAntiforgery csrf, StudioStore store, StructureArchivePdf pdf) =>
        {
            await csrf.ValidateRequestAsync(ctx);
            var request = await ctx.Request.ReadFromJsonAsync<StructureArchiveRequest>(ctx.RequestAborted);
            if (request?.Options == null)
            {
                return Results.BadRequest(new
                {
                    error = "请填写归档信息后重试。"
                });
            }
            var archive = store.ReadStructureArchive(ctx.User, id, request.Revision, request.Options);
            var bytes = await pdf.GenerateAsync(archive, ctx.RequestAborted);
            // 长文档生成期间可能撤销成员或会话；返回文件前再次检查当前授权。
            store.RequireProject(ctx.User, id, ProjectAccess.Read | ProjectAccess.Export, Permission.Export);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, "application/pdf", archive.FileName);
        }).RequireAuthorization();
        app.MapPut(
            "/api/projects/{id}/tables",
            async (string id, HttpContext ctx, IAntiforgery csrf, StudioStore store) =>
        {
            await csrf.ValidateRequestAsync(ctx);
            var request = await ctx.Request.ReadFromJsonAsync<SaveTableRequest>();
            if (request?.Table == null)
            {
                return Results.BadRequest();
            }

            return Results.Ok(store.SaveTable(ctx.User, id, request.Revision, request.Table, request.Delete));
        }).RequireAuthorization();
    }
}

/// <summary>
/// 表定义保存请求。
/// </summary>
/// <param name="Revision">客户端读取的项目修订号，防止覆盖他人修改。</param>
/// <param name="Table">待保存或删除的表定义。</param>
/// <param name="Delete">是否删除设计表；不操作业务数据库。</param>
public record SaveTableRequest(int Revision, TableDesign Table, bool Delete = false);

/// <summary>仅提交归档信息和预期修订号；结构由服务器读取，不能由客户端注入。</summary>
public record StructureArchiveRequest(int Revision, StructureArchiveOptions Options);
