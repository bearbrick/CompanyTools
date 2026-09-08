using System.Net;
using Microsoft.AspNetCore.Antiforgery;

namespace DbStudio.Web;

/// <summary>
/// 独立于 Blazor 会话的登录页模板，负责输出包含防伪令牌的 HTML 表单。
/// </summary>
internal static class LoginPage
{
    /// <summary>
    /// 渲染登录表单；令牌必须先进行 HTML 编码，错误信息使用固定文案。
    /// </summary>
    internal static string Render(AntiforgeryTokenSet token, string error) => $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>登录 · DB Studio</title>
        <link rel="stylesheet" href="/app.css">
        </head>
        <body class="login-page">
        <main class="login-shell">
        <section class="login-intro">
        <div class="brand">
        <img src="/icons/database.svg" alt=""> DB Studio</div>
        <span class="eyebrow">数据库设计工作台</span>
        <h1>让设计，<br>成为共同的语言。</h1>
        <p>从一个字段，到整个项目。<br>集中维护数据库结构，让每次变更都有据可循。</p>
        <div class="login-tags">
        <span>SQL Server</span>
        <span>结构化设计</span>
        <span>团队协作</span>
        </div>
        </section>
        <section class="login-form">
        <span class="eyebrow">WELCOME BACK</span>
        <h2>登录工作台</h2>
        <p class="muted">使用你的团队账号，继续数据库设计。</p>
        <form action="/auth/login" method="post">
        <input type="hidden" name="{{token.FormFieldName}}" value="{{WebUtility.HtmlEncode(token.RequestToken)}}">
        <label>账号<input name="login" autocomplete="username" required autofocus placeholder="输入账号">
        </label>
        <label>密码<input name="password" type="password" autocomplete="current-password" required placeholder="输入密码">
        </label>
        <p class="login-error">{{error}}</p>
        <button class="primary" type="submit">进入工作台</button>
        </form>
        <small>首次运行账号见服务器 App_Data/first-run.txt</small>
        </section>
        </main>
        </body>
        </html>
        """;
}
