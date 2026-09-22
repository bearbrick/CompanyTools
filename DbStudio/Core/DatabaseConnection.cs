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
    /// <summary>连接所属发布环境；旧连接默认归入开发环境。</summary>
    public string Environment { get; set; } = DatabaseEnvironments.Development;
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

/// <summary>一个具有固定推进顺序的数据库发布环境。</summary>
public sealed record DatabaseEnvironment(string Id, string Name, int Order);

/// <summary>数据库环境目录及逐环境推进规则。</summary>
public static class DatabaseEnvironments
{
    /// <summary>允许从当前设计修订建立 V 的开发环境。</summary>
    public const string Development = "development";
    /// <summary>用于功能和集成验证的测试环境。</summary>
    public const string Test = "test";
    /// <summary>生产发布前的预发布环境。</summary>
    public const string Staging = "staging";
    /// <summary>只允许发布已有 V 的生产环境。</summary>
    public const string Production = "production";

    /// <summary>按发布推进顺序排列的全部环境。</summary>
    public static IReadOnlyList<DatabaseEnvironment> All
    {
        get;
    } =
    [
        new(Development, "开发", 10),
        new(Test, "测试", 20),
        new(Staging, "预发布", 30),
        new(Production, "生产", 40)
    ];

    /// <summary>把历史或未知环境安全归入开发环境。</summary>
    public static string Normalize(string? value)
        => All.Any(item => item.Id == value) ? value! : Development;

    /// <summary>取得环境的中文名称。</summary>
    public static string Name(string? value)
        => All.First(item => item.Id == Normalize(value)).Name;

    /// <summary>取得环境的发布顺序。</summary>
    public static int Order(string? value)
        => All.First(item => item.Id == Normalize(value)).Order;

    /// <summary>生产发布时要求用户输入的服务端校验短语。</summary>
    public static string ProductionConfirmation(int releaseVersion) => $"生产 V{releaseVersion}";
}

/// <summary>反推结果和无法无损映射到设计模型的特性。</summary>
public record DatabaseSnapshot(DesignProject Project, List<string> Warnings);

/// <summary>已保存的连接与仅服务端可访问的凭证。</summary>
internal record DatabaseCredential(DatabaseConnection Profile, string ConnectionString);
