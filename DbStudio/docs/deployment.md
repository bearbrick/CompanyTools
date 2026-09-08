# 团队部署与分享地址

当前预览仅监听本机。实际内网/公网主机、域名尚待确定，不能把回环地址当成外部可访问地址。

## 发布构建

```powershell
dotnet publish DbStudio/DbStudio.csproj -c Release -o artifacts/db-studio
```

服务器需要 .NET 10 ASP.NET Core Runtime；Windows IIS 需要对应 Hosting Bundle。运行工作目录使用发布目录。

## 外置数据目录

通过环境变量配置，避免更新发布包覆盖数据：

```text
Studio__DataDirectory=D:\DbStudioData
Studio__PublicBaseUrl=https://dbstudio.example.com
ASPNETCORE_URLS=http://127.0.0.1:5188
```

以上域名为示例，必须替换为实际地址。数据目录包含 SQLite、首次密码、连接加密密钥；限制为应用和运维备份账号访问，迁移时数据库与密钥一起备份。

## 公司内网

在专用服务器上将监听地址改为 `http://0.0.0.0:5188`，PublicBaseUrl 配成实际内网地址。网络策略限定允许的团队网段，推荐公司证书和 HTTPS。

本地开发预览不会自动修改防火墙或扩大监听。确定目标和访问范围后应从另一台机器验证。

## 公网域名

使用 IIS 或 HTTPS 反向代理提供域名和证书，后端仅监听回环地址。Blazor 需要 WebSocket 转发和合理长连接超时。正式分享地址使用配置的 PublicBaseUrl，避免依赖外部 Host 头。

部署检查：匿名工作台跳转登录；匿名项目 API 返回 401；分享正常显示且不能编辑；撤销后返回 404；登录、退出、上传和 WebSocket 正常。

## SQL Server 身份

Windows 集成认证使用应用进程身份，不是浏览器用户。反推需要数据库 `VIEW DEFINITION`，同步需要相应 DDL 权限。业务库应由数据库管理员事先创建。默认验证服务器证书，仅明确使用内网自签名证书时选择信任证书。

## 更新

先备份外置数据目录，停止应用后替换发布包。保留数据目录和密钥；权限/分享表采用幂等迁移。升级后历史项目默认只允许系统管理员，由管理员逐项目添加成员。

回滚前检查数据模型兼容性，不要覆盖正在运行的 SQLite 文件。
