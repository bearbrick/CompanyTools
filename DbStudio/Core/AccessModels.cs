namespace DbStudio.Core;

/// <summary>
/// 可组合的全局操作权限；项目操作还必须通过对应项目的成员授权。
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>无权限。</summary>
    None = 0,

    /// <summary>查看项目、设计与审计。</summary>
    Read = 1,

    /// <summary>维护数据表、字段和约束。</summary>
    Design = 2,

    /// <summary>创建项目。</summary>
    Projects = 4,

    /// <summary>导出项目备份及 SQL 脚本。</summary>
    Export = 8,

    /// <summary>管理用户与角色。</summary>
    Security = 16,

    /// <summary>当前版本定义的全部权限。</summary>
    All = Read | Design | Projects | Export | Security
}

/// <summary>
/// 角色及其全局权限集合。
/// </summary>
public record Role(string Id, string Name, Permission Permissions);

/// <summary>
/// 不包含密码信息的成员资料。
/// </summary>
public record UserInfo(string Id, string Login, string DisplayName, string RoleId, bool Enabled);

/// <summary>
/// 通过安全戳验证后的当前用户与最新权限。
/// </summary>
public record SessionUser(string Id, string Login, string DisplayName, string RoleId, string RoleName, Permission Permissions);

/// <summary>
/// 一条操作审计，Time 使用 UTC ISO 8601 格式。
/// </summary>
public record AuditItem(string Time, string Actor, string Action, string Detail);
