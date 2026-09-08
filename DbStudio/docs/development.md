# 代码结构与维护约定

## 目录职责

| 入口 | 职责 |
| --- | --- |
| `Program.cs` | 服务装配、中间件顺序和路由注册 |
| `Web/ServiceRegistration.cs` | Blazor、Cookie 会话与限流配置 |
| `Web/AuthenticationEndpoints.cs`、`LoginPage.cs` | 登录、退出和登录表单 |
| `Web/ProjectEndpoints.cs`、`RequestPipeline.cs` | 项目 API、防伪验证与 HTTP 错误处理 |
| `Core/*Design.cs`、`DesignProject.cs`、`AccessModels.cs` | DDL 元数据与权限模型，属性注释解释取值含义 |
| `Core/StudioStore.cs` | SQLite 初始化、参数化命令与事务共用工具 |
| `Core/StudioStore.Authentication.cs` | 登录锁定、实时会话验证与密码修改 |
| `Core/StudioStore.Projects.cs` | 项目保存、并发冲突、入站引用保护与导出 |
| `Core/StudioStore.AccessControl.cs`、`StudioStore.Audit.cs` | 用户、角色与审计查询 |
| `Core/SqlServerDdl*.cs` | 方言目录、结构校验、SQL 生成分别维护 |
| `Components/Pages/Home.razor.cs`、`Home.SelectOptions.cs` | 编辑草稿状态、页面操作与选项说明 |
| `Components/StudioSelect.razor` | 全站统一选择器及值提交边界 |
| `wwwroot/studio-select.js`、`studio-select.css` | 浮层定位、本地搜索、键盘操作与共享外观 |

Store 使用 partial 文件组织元数据服务，公开 API 和事务边界保持集中。实际 SQL Server 读取和执行由独立的 `SqlServerCatalog`、`SqlServerTools` 负责，不在设计文档保存时隐式执行数据库命令。

## 约定

- 遵循根目录 `.editorconfig`：四空格、完整控制流大括号、独立语句和统一换行。长调用参数逐行排列。
- 模型属性、服务入口和页面操作使用中文 XML 注释。注释解释取值约束、调用目的、失败行为；权限、版本控制、事务和引用保护另有就地说明。
- 保存前编辑深复制草稿，服务端返回成功后更新快照；所有入口复用服务授权，不能仅靠按钮禁用实现权限控制。
- 账号启用状态、角色权限与安全戳实时读取。项目文档、修订号与审计同事务提交。
- 元数据 SQL 使用参数，DDL 统一转义标识符和文字。数据库同步只执行服务器保存的 DacFx 计划，必须先预览并确认目标，不接受浏览器传来的任意 SQL。

## 选择器

项目、字段类型、模块、外键字段/引用表/参照动作和成员角色共用一个组件。候选项同时支持名称、说明和分类搜索；模块和复合外键字段允许自定义输入。类型与参照动作使用限定值。

搜索与方向键移动在浏览器执行；仅确认选中后回传 Blazor。支持 Enter 确定、Escape 取消、Tab 继续表单、外部点击关闭。弹层自动选择上下方向，滚动外层区域或调整窗口时关闭，避免与原单元格脱离。Popover 使用浏览器顶层绘制以避免被表格滚动容器和弹窗裁切，面向当前版 Edge / Chrome。

参考：[MDN Popover](https://developer.mozilla.org/en-US/docs/Web/API/Popover_API/Using)、[WAI-ARIA Combobox](https://www.w3.org/WAI/ARIA/apg/patterns/combobox/)。

## 验证命令

```powershell
dotnet format CompanyTools.slnx --verify-no-changes --no-restore
dotnet build CompanyTools.slnx
dotnet run --project DbStudio.Checks
```

Visual Studio 占用常规输出时，可用独立目录验证：

```powershell
dotnet build DbStudio.Checks/DbStudio.Checks.csproj -p:UseAppHost=false -o DbStudio.Checks/bin/Preview
dotnet DbStudio.Checks/bin/Preview/DbStudio.Checks.dll
```

检查使用独立临时数据库，覆盖真实服务行为；浏览器验证另外检查下拉搜索、键盘、取消、弹窗与屏幕边缘定位。

### 备份恢复扩展

`ProjectBackup` 严格解析外部文档并检查结构；`StudioStore.Import` 统一授权、重校验、ID/外键重映射和事务保存；`Home.Import` 管理文件读取版本、预览失效与导入后的页面切换。导入只新增独立项目。变更 JSON 原文后必须重新预览，服务端提交时仍再次检查原文。

## 项目管理、分享和数据库工具

| 文件 | 职责 |
| --- | --- |
| `StudioStore.ProjectAccess.cs` | 项目成员、权限检查、项目信息、空模块和版本提交 |
| `StudioStore.Sharing.cs` | 随机令牌摘要、有效期、撤销和匿名单项目读取 |
| `Web/ShareEndpoints.cs`、`SharedProjectPage.razor` | 无编辑会话的只读 HTML、表格/卡片和安全响应头 |
| `ProjectSettings.razor` | 基本信息、模块、成员与分享管理 |
| `StudioStore.Connections.cs` | 项目连接、凭证加密与反推结果合并 |
| `SqlServerCatalog.cs` | 只读系统目录映射及无法无损反推的特性提示 |
| `SqlServerTools.cs` | DacFx 包、差异报告、计划期限、结构漂移检查和部署 |
| `DatabaseToolsPanel.razor` | 连接配置、反推预览、差异和执行确认、取消操作 |
| `DbStudio.Checks/DatabaseChecks.cs` | 实际 SQL Server 建表、增量同步、反推、删除保护的集成验证 |

业务授权顺序：有效会话 → 全局角色 → 项目权限 → 对象项目归属 → 当前版本。跨项目 ID、已撤销成员和分享令牌都不能绕过服务入口。只有内置系统管理员可查看全局历史审计。

对外部署配置及剩余环境验收见 `deployment.md`。本机回环地址不能替代真实分享域名验收。
