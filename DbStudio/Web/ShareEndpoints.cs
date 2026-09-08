using DbStudio.Components.Pages;
using DbStudio.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DbStudio.Web;

/// <summary>无登录的只读 HTML 页面；每次 GET 都重新检查分享令牌，独立于编辑工作台。</summary>
internal static class ShareEndpoints
{
    internal static void MapStudioShares(this WebApplication app)
    {
        app.MapGet("/share/{token}", (string token, HttpContext context, StudioStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            var project = store.SharedProject(token);
            if (project == null)
            {
                return (IResult)Results.Content("分享不存在、已撤销或已过期。请向项目管理员索取有效链接。", "text/plain; charset=utf-8", statusCode: 404);
            }
            return new RazorComponentResult<SharedProjectPage>(new
            {
                Project = project,
                Token = token,
                TableId = context.Request.Query["table"].ToString(),
                Search = context.Request.Query["q"].ToString(),
                View = context.Request.Query["view"] == "cards" ? "cards" : "table"
            });
        }).AllowAnonymous();
    }
}
