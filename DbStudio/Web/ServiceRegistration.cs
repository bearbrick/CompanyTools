using System.Threading.RateLimiting;
using DbStudio.Core;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DbStudio.Web;

/// <summary>
/// 应用依赖、Cookie 会话与登录限流的集中配置。
/// </summary>
internal static class ServiceRegistration
{
    /// <summary>
    /// 注册交互组件和存储服务；Cookie 校验读取最新安全戳以撤销失效会话。
    /// </summary>
    internal static void AddStudioServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Events.OnValidatePrincipal = context =>
            {
                if (context.HttpContext.RequestServices.GetRequiredService<StudioStore>().Current(context.Principal!) == null)
                {
                    context.RejectPrincipal();
                }

                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<StudioStore>();
        builder.Services.AddSingleton<SqlServerTools>();
        builder.Services.AddSingleton<StructureArchivePdf>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy(
                "login",
                context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
