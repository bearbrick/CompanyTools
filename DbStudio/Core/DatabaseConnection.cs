namespace DbStudio.Core;

/// <summary>项目数据库连接的非敏感显示信息。密码只在提交时传入，永不回显。</summary>
public sealed class DatabaseConnection
{
    /// <summary>连接稳定标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>所属项目。</summary>
    public string ProjectId { get; set; } = "";
    /// <summary>连接显示名称。</summary>
    public string Name { get; set; } = "开发数据库";
    /// <summary>SQL Server 实例地址。</summary>
    public string Server { get; set; } = "";
    /// <summary>明确指定的目标数据库，不允许系统库。</summary>
    public string Database { get; set; } = "";
    /// <summary>使用应用服务账号的 Windows 集成认证。</summary>
    public bool IntegratedSecurity { get; set; } = true;
    /// <summary>SQL 登录账号，集成认证时为空。</summary>
    public string UserName { get; set; } = "";
    /// <summary>是否信任服务器证书；默认验证证书。</summary>
    public bool TrustServerCertificate
    {
        get; set;
    }
    /// <summary>连接配置版本，用于使旧的同步计划失效。</summary>
    public int Revision
    {
        get; set;
    }
}

/// <summary>反推结果和无法无损映射到设计模型的特性。</summary>
public record DatabaseSnapshot(DesignProject Project, List<string> Warnings);

/// <summary>已保存的连接与仅服务端可访问的凭证。</summary>
internal record DatabaseCredential(DatabaseConnection Profile, string ConnectionString);
