using Microsoft.AspNetCore.Antiforgery;

namespace DbStudio.Web;

/// <summary>
/// 统一响应安全标头和可预期的请求错误。
/// </summary>
internal static class RequestPipeline
{
    /// <summary>
    /// 将授权、防伪及 API 业务校验错误转换为明确的 HTTP 响应。
    /// </summary>
    internal static void UseStudioRequestHandling(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            try
            {
                await next();
            }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "请求校验失败，请刷新后重试。" }); }
            catch (UnauthorizedAccessException) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "没有此操作权限或登录已失效。" }); }
            catch (InvalidOperationException ex) when (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
        });
    }
}
