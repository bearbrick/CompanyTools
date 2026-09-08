# DB Studio · IIS 部署与运维手册

适用：.NET 10、Blazor Interactive Server、Windows x64、IIS 10。本文以 Windows Server 2022、独立网站、独立应用程序池为例；目录、域名和端口均为示例，请按实际环境替换。

部署或启动 Studio 不会自动修改业务 SQL Server。业务库结构变更仍需在网页中比对、核对并执行。

## 1. 部署方案与目录规划

```text
浏览器 → HTTPS / IIS → DB Studio（应用池内运行）
                         ├─ SQLite：设计、账号、权限、审计、连接配置
                         └─ SQL Server：通过项目连接访问实际业务数据库
```

| 项目 | 本文示例 | 用途 |
| --- | --- | --- |
| IIS 网站名称 | `DBStudio` | 浏览器访问入口 |
| 应用程序池 | `DBStudioPool` | 仅运行本应用 |
| 发布目录 | `D:\DbStudio\Releases\20260908-01` | 每个版本独立目录，便于回滚 |
| 数据目录 | `D:\DbStudio\Data` | 固定不变，不随版本切换 |
| 日志目录 | `D:\DbStudio\Logs` | 临时启动日志 |
| 备份目录 | `D:\DbStudio\Backups` | 停机备份，另复制到独立备份存储 |
| 正式地址 | `https://dbstudio.example.com` | 替换为公司的实际域名 |
| 临时验证地址 | `http://服务器内网IP:8088` | 仅供受限内网首次验证 |

**Studio 本地数据与业务数据库是两回事。** Studio 使用 SQLite，不需要为 Studio 新建 SQL Server 数据库。业务数据库连接在网页内配置。

当前版本按**单台服务器、单个应用实例**部署：应用池最大工作进程数保持 `1`，不配置 Web Garden，不让两个站点同时写同一个数据目录。设计操作锁、Blazor 会话及待执行计划包含进程内状态，不能直接通过多个实例实现负载均衡。SQLite 数据目录使用本地磁盘，不放到 SMB 共享盘。

当前应用的页面、登录、API 使用根路径 `/`。请使用独立域名或独立端口的网站；本手册不采用 `https://公司网站/dbstudio/` 这样的子路径部署方式。

## 2. 安装 IIS 与 .NET 10

### 2.1 安装 IIS 角色

服务器管理器 → **添加角色和功能 → 基于角色或基于功能的安装 → Web 服务器（IIS）**，确认安装：

- Web 服务器：静态内容、默认文档、HTTP 错误、HTTP 日志、请求筛选。
- 应用程序开发：**WebSocket 协议**。
- 管理工具：**IIS 管理控制台**。
- 可选：**应用程序初始化**，用于网站预加载。

Windows Server 上也可在管理员 Windows PowerShell 中执行：

```powershell
Install-WindowsFeature Web-Server,Web-WebSockets,Web-Mgmt-Console,Web-AppInit -IncludeManagementTools
```

`Install-WindowsFeature` 是服务器命令。Windows 11 本机验证请通过“启用或关闭 Windows 功能”安装 IIS 与 WebSocket 协议。

### 2.2 安装 Hosting Bundle

先装 IIS，再从 [.NET 10 下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) 的 **ASP.NET Core Runtime → Windows → Hosting Bundle** 下载当前受支持的正式版本，以管理员身份安装。

服务器只运行发布包时不需要 SDK；开发机或构建机需要 .NET 10 SDK。只装普通 Runtime、Desktop Runtime 或 SDK，不等于已安装 IIS 托管模块。若先装 Hosting Bundle、后装 IIS，重新运行 Hosting Bundle 安装程序进行修复。[微软安装说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0)

安装后按提示重启服务器，或在维护窗口重启 IIS 服务。下面的命令会影响服务器上其他 IIS 网站，不作为日常单站点升级命令：

```powershell
net stop was /y
net start w3svc
```

在新开的管理员终端验证：

```powershell
dotnet --list-runtimes
Test-Path 'C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
```

应包含 `Microsoft.NETCore.App 10.0.x`、`Microsoft.AspNetCore.App 10.0.x`，模块文件检查应为 `True`。安装当前 .NET 10 服务补丁，不使用预览版。

## 3. 生成并复制发布包

在开发机的仓库根目录执行，每次使用新的输出目录：

```powershell
dotnet publish DbStudio/DbStudio.csproj -c Release -r win-x64 --self-contained false -p:UseAppHost=false -o artifacts/iis-publish-20260908-01
```

这是依赖服务器运行时的 Windows x64 发布；`UseAppHost=false` 使启动入口统一为 `dotnet .\DbStudio.dll`。不要只复制 Debug 下的 DLL，也不要把源码目录设为网站物理路径。

发布目录根部应包含：

```text
web.config
DbStudio.dll
DbStudio.deps.json
DbStudio.runtimeconfig.json
DbStudio.staticwebassets.endpoints.json
appsettings.json
wwwroot\
以及全部依赖 DLL、资源目录和原生库
```

SQLite 和 DacFx 依赖随发布包携带，不需要另外为应用安装 SQL Server、LocalDB、SSMS 或 SqlPackage。不要手工删减不熟悉的 DLL，SQLite、SQL Client 的原生依赖也要一起部署。

把**整个发布目录的内容**复制到服务器 `D:\DbStudio\Releases\20260908-01`。网站物理路径直接指向含 `web.config` 的这一层。Web SDK 发布时自动生成该文件，它是 IIS 启动应用的入口。[微软发布配置说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/web-config?view=aspnetcore-10.0)

交付前确认包内没有 `studio.db`、`first-run.txt`、`App_Data` 或 `keys`。代码包不等于现有系统备份；数据按第 7 节迁移，不把运行目录和凭证提交到 Git。

## 4. 创建目录和应用程序池

在服务器管理员 PowerShell 中创建目录：

```powershell
New-Item -ItemType Directory -Force -Path 'D:\DbStudio\Releases\20260908-01','D:\DbStudio\Data','D:\DbStudio\Logs','D:\DbStudio\Backups'
```

IIS 管理器 → **应用程序池 → 添加应用程序池**：

| 设置 | 值 |
| --- | --- |
| 名称 | `DBStudioPool` |
| .NET CLR 版本 | **无托管代码 / No Managed Code** |
| 托管管道模式 | 集成 / Integrated |
| 自动启动 | 开启 |

“无托管代码”表示不使用旧版 .NET Framework CLR，由 ASP.NET Core Module 托管 .NET 10。[IIS 托管说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0)

在该池的 **高级设置** 中配置：

| 设置 | 建议值 | 说明 |
| --- | --- | --- |
| 启用 32 位应用程序 | `False` | 与 win-x64 发布包一致 |
| 标识 | `ApplicationPoolIdentity` | 独立运行身份；域账号方案见第 9 节 |
| 加载用户配置文件 / Load User Profile | `True` | 为宿主默认用户配置、Cookie 数据保护等提供持久环境 |
| 最大工作进程数 | `1` | 当前版本不支持多进程共享运行状态 |
| 启动模式 / Start Mode | `AlwaysRunning` | 减少首次访问等待 |
| 空闲超时（分钟） | `0` | 避免空闲关停造成冷启动 |
| 禁止重叠回收 / Disable Overlapped Recycle | `True` | 避免回收期间两个实例同时使用同一 SQLite |
| 回收计划 | 维护时段 | 回收会中断未保存编辑和待执行计划 |

应用池创建后，再授予目录权限：

```powershell
icacls 'D:\DbStudio\Releases\20260908-01' /grant 'IIS AppPool\DBStudioPool:(OI)(CI)RX' /T
icacls 'D:\DbStudio\Data' /grant 'IIS AppPool\DBStudioPool:(OI)(CI)M' /T
icacls 'D:\DbStudio\Logs' /grant 'IIS AppPool\DBStudioPool:(OI)(CI)M' /T
```

发布目录授予读取和执行；Data 和 Logs 授予修改。SQLite 需要创建、写入和删除 WAL/SHM 文件，只给 `studio.db` 单个文件写权限不够。备份目录只授予运维及备份账号。检查继承权限，移除不必要的普通用户访问，不使用 `Everyone` 完全控制。

本项目的**业务连接凭证**使用 `Data\keys` 下的独立数据保护密钥；`Load User Profile` 不能代替备份该目录。登录 Cookie 使用 ASP.NET Core 宿主的数据保护配置，迁移机器或身份后可能需要重新登录。

## 5. 配置运行参数

### 5.1 appsettings.Production.json

在服务器发布目录根部创建 `appsettings.Production.json`：

```json
{
  "AllowedHosts": "dbstudio.example.com",
  "Studio": {
    "DataDirectory": "D:\\DbStudio\\Data",
    "PublicBaseUrl": "https://dbstudio.example.com",
    "LoadLegacySeed": false
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

| 配置 | 说明 |
| --- | --- |
| `Studio:DataDirectory` | 使用绝对路径；默认是发布目录下 App_Data，换版本目录时容易误建新库 |
| `Studio:PublicBaseUrl` | 完整浏览器访问地址，用于分享链接；不会自动创建域名、证书或绑定 |
| `AllowedHosts` | 主机名，不含协议、路径或端口；多个值用分号分隔 |
| `Studio:LoadLegacySeed` | 正常部署保持 false；历史种子不是迁移现有数据的方法 |
| `Studio:InitialPassword` | 可选，仅空库首次初始化生效；建议省略，让系统生成随机密码 |

临时用 `http://192.168.10.20:8088` 验证时，把 PublicBaseUrl 改为该地址，AllowedHosts 改为 `192.168.10.20`；正式上线时改回实际域名。域名绑定或 AllowedHosts 限制后，直接访问 localhost/IP 可能无法访问该站点。

环境变量可覆盖 JSON，对应名称为 `Studio__DataDirectory`、`Studio__PublicBaseUrl`，使用双下划线。配置不生效时，检查系统、应用池、web.config 是否还有旧环境变量。`appsettings.Local.json` 不是当前程序自动加载的配置文件，不用它配置服务器。

### 5.2 web.config

保留发布自动生成的处理器配置。按本文发布命令，可将生成的 `<aspNetCore ... />` 展开，明确生产环境和外置启动日志路径：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*"
             modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\DbStudio.dll"
                  hostingModel="inprocess"
                  stdoutLogEnabled="false"
                  stdoutLogFile="D:\DbStudio\Logs\stdout">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
```

该示例用于本文的框架依赖发布；若以后改变发布方式，应以新生成的文件为基础修改。若服务器配置了 `DOTNET_ENVIRONMENT`，也应确保与 Production 一致。

**IIS 下端口由“网站绑定”决定。** 不需要 `dotnet run`、ASPNETCORE_URLS、5188 端口或额外后台 dotnet 进程。本文使用 IIS 进程内托管，不要求 ARR 或 URL Rewrite 代理到 Kestrel。

## 6. 创建网站、网络和 HTTPS

IIS 管理器 → **网站 → 添加网站**：

1. 网站名称填 `DBStudio`，应用池选择 `DBStudioPool`。
2. 物理路径填 `D:\DbStudio\Releases\20260908-01`。
3. 首次验证可选 HTTP、端口 8088、主机名留空；也可直接配置正式 HTTPS。
4. 需要迁移旧数据时，先不要启动，完成第 7 节后再启动。

网站的 **身份验证** 中保持**匿名身份验证启用**，匿名用户身份可设为“应用程序池标识”，与前面的 ACL 对应。Studio 使用自己的登录页和 RBAC，匿名连接 IIS 不等于可以匿名编辑。不要为了连接 SQL Server 而启用 IIS Windows 身份验证。

确认 WebSocket 角色已安装，网站的 WebSocket 设置未被上级配置禁用。Blazor 交互连接走 `/_blazor`；WebSocket 是推荐传输，失败时可能退回长轮询。首页能显示不代表交互连接正常。[WebSocket 配置说明](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)

若安装了“应用程序初始化”，可在网站高级设置中开启“预加载”，配合应用池 AlwaysRunning。它减少冷启动，不会消除结构比对时的数据库网络及整库提取耗时。

正式 HTTPS 上线：

1. 公司 DNS 创建实际域名，解析到 IIS 服务器 IP。
2. IIS“服务器证书”中导入或申请对应证书，证书包含私钥，并被客户端信任。
3. 网站添加 HTTPS、443、实际主机名，选择证书；同一 IP 上多个 HTTPS 网站通常需启用 SNI。
4. Windows 防火墙及上游网络策略开放所需入站端口，访问范围限定到使用者网段。
5. 更新 PublicBaseUrl 和 AllowedHosts，从另一台电脑验证域名。

当前代码没有自动 HTTP→HTTPS 跳转。正式环境可只保留 HTTPS 绑定；若保留 HTTP，应单独配置跳转。外层还有网关时，需额外配置网关长连接与 WebSocket，本手册以直接 IIS 托管为准。

网络方向需区分：浏览器访问 IIS 的 443/8088；**IIS 服务器主动访问业务 SQL Server 的实际 TCP 端口**。SQL 端口不一定是 1433，应填写 DBA 提供的 `服务器,端口`。

## 7. 迁移现有数据与全新初始化

### 7.1 带上当前开发环境的数据

若要保留现有设计、账号、权限、审计和连接，迁移的是正在使用的整个数据目录。未改配置时源目录是仓库下 `DbStudio\App_Data`；设置过 DataDirectory 时以实际配置为准。

1. 通知使用者保存，等待正在执行的数据库同步结束。
2. 停止所有使用源目录的进程，包括 IDE 调试、预览服务及 IIS。只关闭浏览器不够。
3. 停止目标站点和应用池，确认目标 Data 是本次准备的空目录；若已有数据，先独立备份，不混合覆盖。
4. 将源数据目录的**全部内容**复制到服务器 `D:\DbStudio\Data`。
5. 确认有 `studio.db`，已保存连接对应的 `keys` 目录也完整。若有 `studio.db-wal`、`studio.db-shm`，一并保留，不单独丢弃 WAL。
6. 对目标 Data 重新应用 ACL，配置 DataDirectory，再启动站点。

例如已把停机复制的资料送到服务器 `D:\Transfer\DBStudio-App_Data`，复制到空目标目录：

```powershell
robocopy 'D:\Transfer\DBStudio-App_Data' 'D:\DbStudio\Data' /E /COPY:DAT /DCOPY:DAT /R:2 /W:2
if ($LASTEXITCODE -ge 8) { throw '数据复制失败，请检查 robocopy 输出。' }
```

迁移后用原账号密码登录，InitialPassword 不会重置已有管理员。若看到“我的第一个项目”和新的首次密码，先停止应用，检查是否指错目录，不把它当成正常迁移完成。

仅恢复 studio.db 不完整：keys 丢失会导致已有连接凭证无法解密。迁移后逐个测试连接；若旧密钥另受原机器/账号保护，应恢复相应保护环境，或由管理员重新录入连接密码。Studio 登录密码与业务数据库连接密码彼此独立。

### 7.2 全新安装

保持 Data 为空、LoadLegacySeed=false，启动并访问网站。程序创建 SQLite、角色、空白项目和管理员：

- 用户名：`admin`。
- 随机密码：服务器 `D:\DbStudio\Data\first-run.txt`。
- 首次登录后通过账号菜单修改密码，妥善保管后删除首次密码文件。
- 找不到首次密码时，检查启动错误和实际数据目录，不删除 studio.db 来“找回密码”。

日常运行不依赖 Excel。项目 JSON 导入只恢复设计并创建新项目，不含账号、成员、分享、连接，不能代替完整迁移。

## 8. 上线验收

先在服务器验证，再从同事电脑验证：

| 操作 | 预期结果 |
| --- | --- |
| 访问网站根地址 | 未登录时进入登录流程，无 500/502 |
| 登录、退出、重新登录 | 会话正常，样式图标完整 |
| 展开下拉框，修改一个测试字段并保存 | 交互正常，刷新后修改仍在 |
| 开发者工具 Network 查看 `/_blazor` | WebSocket 成功；HTTP/1.1 时通常显示 101，无持续重连 |
| 查看迁入项目、用户及成员 | 与原环境一致 |
| 只读成员打开项目 | 能查看，不能保存或同步 |
| 匿名访问 `/api/projects` | 不返回项目内容，正常为 401 |
| 从另一台电脑打开分享链接 | 正式域名、只读访问；撤销后失效 |
| 测试业务库连接 | 连接指定实例和业务数据库 |
| 当前表“结构比对” | 显示目标、范围、差异和阶段耗时 |

部署验收检查预览即可，不必执行业务库 DDL。不要将 `DbStudio.Checks` 的真实数据库集成检查用于业务库验收：该测试会在所选实例创建专属测试数据库，应只在测试环境运行。

## 9. IIS 下连接 SQL Server

### 9.1 SQL 登录认证

在网页数据库工具中新增连接，填写服务器与端口、已有业务数据库、SQL 账号密码。SQL Server 需启用对应认证及 TCP 访问，账号由 DBA 配置权限。密码由应用加密保存到 SQLite，不放进生产 JSON 配置。

在 IIS 服务器检查网络，替换示例地址和端口：

```powershell
Test-NetConnection -ComputerName 'sqlserver.example.com' -Port 1433
```

网络通不等于身份和权限正确，之后仍需在 Studio 测试连接。

### 9.2 Windows 集成认证

Windows 集成认证使用 **IIS 应用池进程身份**，不是网页账号 admin，也不是开发者的 Windows 身份。

- SQL 在同机：DBA 可为 `IIS AppPool\DBStudioPool` 创建 Windows 登录及数据库用户。
- SQL 在另一台域内服务器：默认应用池虚拟身份通常以 IIS 服务器的机器账号访问网络。可由运维改用专用域服务账号或配置 gMSA，并由 DBA 授权该身份。
- 改应用池身份后，为新身份重新授予程序、Data、Logs 权限；普通域账号密码按公司策略管理。
- 不把开发机 `(localdb)\...` 连接用于正式 IIS。LocalDB 是用户环境，不能假设应用池能访问开发者实例。

### 9.3 操作权限与证书

| 操作 | 权限考虑 |
| --- | --- |
| 测试连接 | 能登录并访问所选库 |
| 反推 | 能读取系统目录，具备相应元数据可见性，例如数据库 VIEW DEFINITION |
| 比对 | DacFx 模型提取可能需要比目录查询更多的权限；由 DBA 按提取错误及 SQL Server 版本配置 |
| 同步 | 需要计划涉及的 DDL 权限，含必要的表、索引、约束、引用和扩展属性权限 |

测试连接成功不代表所有功能都有权限。微软通用 DACPAC 提取文档列出服务器与数据库层面的要求，本应用也设置了忽略权限和登录映射等选项，应由 DBA 在目标环境核实。不要为消除报错直接授予 sysadmin。[DACPAC 提取权限参考](https://learn.microsoft.com/en-us/sql/tools/sql-database-projects/concepts/data-tier-applications/extract-dacpac-from-database)

SQL Client 默认验证服务器证书。优先使用受信任证书及正确服务器名；确认是公司内网自签名证书时，才勾选“信任服务器证书”。网站 HTTPS 与 SQL 连接证书分别配置。

## 10. 升级、备份和回滚

### 10.1 更新步骤

1. 通知使用者保存，确认没有正在执行的数据库同步，安排停机。
2. 发布新版本到新的 `Releases\版本号`，暂不切换网站。
3. 复制原服务器的 appsettings.Production.json 到新目录；以新生成的 web.config 为基础保留生产环境、日志等设置。
4. 给新程序目录授予应用池读取和执行权限。
5. 停止网站和应用池，确认进程退出，备份完整 Data、生产配置及旧版本标识。
6. 修改网站物理路径为新发布目录，DataDirectory 始终指向原 Data。
7. 启动后按第 8 节验收，保留旧版本及停机备份。

下面为服务器管理员**分步执行**的辅助命令，不是一键部署脚本。先替换版本路径，备份或验收失败时不继续：

```powershell
Import-Module WebAdministration
Stop-Website -Name 'DBStudio'
Stop-WebAppPool -Name 'DBStudioPool'
Get-WebAppPoolState -Name 'DBStudioPool'
```

确认状态为 Stopped，并在 IIS 工作进程视图确认该池进程已退出。未退出时等待正常结束，不在数据库同步中强行覆盖文件。

停机后备份：

```powershell
$dbStudioBackup = Join-Path 'D:\DbStudio\Backups' (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $dbStudioBackup
robocopy 'D:\DbStudio\Data' (Join-Path $dbStudioBackup 'Data') /E /COPY:DAT /DCOPY:DAT /R:2 /W:2
if ($LASTEXITCODE -ge 8) { throw '备份失败，不要切换版本。' }
Copy-Item -LiteralPath 'D:\DbStudio\Releases\20260908-01\appsettings.Production.json','D:\DbStudio\Releases\20260908-01\web.config' -Destination $dbStudioBackup
```

确认备份完整，再通过 IIS 管理器修改物理路径；或执行以下示例，新目录必须已经准备好：

```powershell
Set-ItemProperty 'IIS:\Sites\DBStudio' -Name physicalPath -Value 'D:\DbStudio\Releases\20260909-01'
Start-WebAppPool -Name 'DBStudioPool'
Start-Website -Name 'DBStudio'
```

不要对 Data 使用带删除能力的镜像覆盖，也不要在运行中单独复制 studio.db 充当完整备份。需要不停机备份时，应采用 SQLite Backup API 等一致性备份方式并备份密钥；本手册以停机复制为准。

重启会清空内存中尚未执行的同步计划，需重新比对。计划本来只有 15 分钟有效期，这不意味着设计数据丢失。

### 10.2 回滚

验收失败时，停止新版本并保存日志和当前数据副本。先确认旧程序是否兼容升级后的 SQLite 和设计文档格式：兼容时可切回旧程序目录；不兼容时需要恢复升级前**完整 Data 与匹配程序版本**，不能只退 DLL。

恢复旧数据会丢弃升级后在 Studio 中保存的修改，需核对影响。恢复到独立空目录并重设 ACL，再切换配置，不覆盖运行中的库。回滚 Studio 不会回滚已执行到业务 SQL Server 的 DDL，业务库恢复由 DBA 单独处理。

## 11. 故障排查

先看 **Windows 事件查看器 → Windows 日志 → 应用程序** 中 IIS ASP.NET Core Module V2、.NET Runtime 等事件。需要启动细节时，临时将 web.config 的 `stdoutLogEnabled` 设为 `true`，重启该站点并复现；日志在 `D:\DbStudio\Logs`，应用池需要修改权限。查完恢复 false，stdout 不自动轮转，不长期积累。[ANCM 日志说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/aspnet-core-module?view=aspnetcore-10.0)

| 现象 | 优先检查 |
| --- | --- |
| 500.19 配置无效 | Hosting Bundle、模块、XML 格式及上级配置锁定 |
| 500.30 启动失败 | 事件/stdout 日志、Data 权限、JSON 格式、SQLite 状态 |
| 500.31 运行时或依赖缺失 | .NET 10 ASP.NET Core Runtime、完整发布包 |
| 500.32 加载/位数错误 | win-x64 与应用池 32 位开关是否一致 |
| 500.35 多应用共用进程 | 使用独立应用程序池 |
| 502.5 进程失败 | 是否改用了进程外配置，启动命令和运行时是否正确 |
| 503 服务不可用 | 应用池状态、身份密码、快速失败保护 |
| 页面显示但按钮无效、不断重连 | WebSocket、`/_blazor` 请求、代理长连接、应用池回收、浏览器控制台 |
| CSS/图标或资源 404 | 是否完整复制、物理目录层级、是否用了不支持的子路径 |
| 400 Invalid Hostname | 绑定、访问地址和 AllowedHosts |
| SQLite Error 14 或只读错误 | Data 目录与 WAL/SHM 权限、目录是否存在 |
| database is locked | 是否有 IDE、第二个站点、多工作进程或工具同时操作 |
| 老项目或账号不见了 | DataDirectory 是否指错、原 App_Data 是否迁移 |
| 连接凭证解密失败 | keys 是否完整、运行身份是否能读，必要时重新录入密码 |
| SQL 登录失败/超时 | IIS 到 SQL 的网络、端口、认证和身份映射 |
| 比对慢，其他页面正常 | 查看阶段耗时；当前单表仍提取整库依赖，预加载不能解决这部分 |
| 分享地址是 localhost/旧域名 | PublicBaseUrl、生效配置及环境变量覆盖 |
| 升级后需重新登录/比对 | 应用池回收、身份/密钥变化或计划过期 |

若需要绕开 IIS 排查启动错误，先停止网站及应用池，确保不会同时打开同一数据目录。在发布目录终端运行：

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
dotnet .\DbStudio.dll --urls http://127.0.0.1:5190
```

该命令会按生产配置启动并访问配置的数据目录，仅用于停机排查。完成后 Ctrl+C 退出，再启动 IIS。终端账号与应用池不同，因此“命令行能跑”不能证明应用池 ACL 或 SQL Windows 身份正确。

## 12. 上线交接记录

记录实际域名、服务器、网站/应用池名称、运行身份、Git 提交号或版本号、程序目录、数据目录、备份位置和维护窗口。密码、首次密码文件及密钥按公司凭证流程单独保管，不写入交接记录或 Git。

本文发布命令及配置样例已按仓库代码核对，验证可生成 IIS 进程内启动配置；目标服务器的角色、证书、域身份、网络和 ACL 需现场按步骤验收。
