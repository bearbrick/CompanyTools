namespace DbStudio.Core;

/// <summary>项目内的权限上限；操作还必须满足账号的全局角色权限。</summary>
[Flags]
public enum ProjectAccess
{
    /// <summary>不可访问。</summary>
    None = 0,
    /// <summary>查看结构。</summary>
    Read = 1,
    /// <summary>维护结构和模块。</summary>
    Design = 2,
    /// <summary>导出结构。</summary>
    Export = 4,
    /// <summary>维护项目信息、成员和分享。</summary>
    Manage = 8,
    /// <summary>连接业务数据库、比对和同步。</summary>
    Database = 16,
    /// <summary>项目管理员。</summary>
    All = Read | Design | Export | Manage | Database
}

/// <summary>项目成员的显示资料，不包含登录凭证。</summary>
public record ProjectMember(string UserId, string Login, string DisplayName, ProjectAccess Access);

/// <summary>分享管理列表；不暴露令牌或令牌摘要。</summary>
public record ProjectShare(string Id, string Name, string CreatedAt, string ExpiresAt, bool Revoked);
