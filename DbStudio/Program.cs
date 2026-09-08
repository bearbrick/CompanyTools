using DbStudio.Components;
using DbStudio.Core;
using DbStudio.Web;

// 1. 装配服务。显式加载静态资源，使本地以 Production 环境启动时也能使用资源映射。
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.AddStudioServices();

// 2. 启动时初始化本地库；初始化失败应立即暴露，避免首个请求才出现错误。
var app = builder.Build();
_ = app.Services.GetRequiredService<StudioStore>();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// 3. 保持中间件顺序：异常处理包住认证、授权、限流和防伪验证。
app.UseStudioRequestHandling();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

// 4. 页面与 API 共享服务层规则，避免不同入口产生不同的权限或结构校验行为。
app.MapStaticAssets();
app.MapStudioAuthentication();
app.MapStudioProjects();
app.MapStudioShares();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
