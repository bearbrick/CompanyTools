using DbStudio.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DbStudio.Web;

/// <summary>
/// 登录与退出路由。所有改变认证状态的请求必须验证防伪令牌。
/// </summary>
internal static class AuthenticationEndpoints
{
    /// <summary>
    /// 映射认证路由；登录还受 IP 限流及服务层账号锁定保护。
    /// </summary>
    internal static void MapStudioAuthentication(this WebApplication app)
    {
        app.MapGet(
            "/login",
            (HttpContext ctx, IAntiforgery csrf) =>
        {
            var token = csrf.GetAndStoreTokens(ctx);
            var error = ctx.Request.Query.ContainsKey("error") ? "账号或密码不正确、账号被禁用，或失败次数过多（请 10 分钟后重试）。" : "";
            return Results.Content(LoginPage.Render(token, error), "text/html; charset=utf-8");
        });
        app.MapPost(
            "/auth/login",
            async (HttpContext ctx, IAntiforgery csrf, StudioStore store) =>
        {
            await csrf.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync();
            var principal = store.Login(form["login"].ToString(), form["password"].ToString());
            if (principal == null)
            {
                return Results.LocalRedirect("/login?error=1");
            }

            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.LocalRedirect("/");
        }).RequireRateLimiting("login");
        app.MapPost(
            "/auth/logout",
            async (HttpContext ctx, IAntiforgery csrf) => { await csrf.ValidateRequestAsync(ctx); await ctx.SignOutAsync(); return Results.LocalRedirect("/login"); });
    }
}
