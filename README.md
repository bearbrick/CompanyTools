# CompanyTools

公司内部工具集。当前包含 **DB Studio**：基于 .NET 10 和 Blazor 的 SQL Server 数据库设计工作台。

## DB Studio

- 项目、模块、表和字段维护，支持主键、索引、外键及检查约束。
- 全局 RBAC 与项目成员权限，项目数据隔离。
- SQL Server 连接管理、反推设计、结构差异比对和同步。
- 免登录只读分享，支持表格和卡片展示、有效期和撤销。
- SQLite 持久化，日常使用不依赖 Excel。

## 本地运行

安装 .NET 10 SDK，然后在仓库根目录执行：

```powershell
dotnet run --project DbStudio --no-launch-profile --urls http://127.0.0.1:5188
```

浏览器访问 <http://127.0.0.1:5188>。首次账号为 `admin`，随机密码生成在 `DbStudio/App_Data/first-run.txt`；运行数据和密钥不提交到仓库。

## 构建与检查

```powershell
dotnet build CompanyTools.slnx
dotnet run --project DbStudio.Checks
dotnet publish DbStudio/DbStudio.csproj -c Release -o artifacts/db-studio
```

真实 SQL Server 集成检查的专用实例配置见应用说明。

## 文档

- [功能与使用说明](DbStudio/README.md)
- [IIS 部署与运维手册](DbStudio/docs/deployment.md)（环境安装、站点配置、数据迁移、备份升级与故障排查）
- [开发说明](DbStudio/docs/development.md)

已有环境部署时应单独迁移 `App_Data` 及其中的密钥；Git 仓库和发布输出不包含现有账号及项目数据库。
