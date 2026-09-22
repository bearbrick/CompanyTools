using DbStudio.Core;

/// <summary>验证不可变 V 必须按配置环境逐级推进。</summary>
internal static class ReleasePromotionChecks
{
    /// <summary>覆盖缺失环境、版本不一致、失败状态和完整链路。</summary>
    internal static void Run(Action<string, bool> check)
    {
        var development = Status("dev", DatabaseEnvironments.Development, "succeeded", 4);
        var test = Status("test", DatabaseEnvironments.Test, "succeeded", 4);
        var staging = Status("staging", DatabaseEnvironments.Staging, "succeeded", 4);
        var production = Status("production", DatabaseEnvironments.Production, "never", null);

        check("Development can establish a release without a lower environment",
            ReleasePromotionPolicy.BlockReason("dev", DatabaseEnvironments.Development, 4, [development]) == "");
        check("Test promotion requires a configured lower environment",
            ReleasePromotionPolicy.BlockReason("test", DatabaseEnvironments.Test, 4, [test]) != "");
        check("The same release can advance from development to test",
            ReleasePromotionPolicy.BlockReason("test", DatabaseEnvironments.Test, 4, [development, test]) == "");
        check("Production promotion rejects a mismatched lower environment",
            ReleasePromotionPolicy.BlockReason("production", DatabaseEnvironments.Production, 4,
                [development, test with { ReleaseVersion = 3 }, staging, production]) != "");
        check("Production promotion rejects a failed lower environment",
            ReleasePromotionPolicy.BlockReason("production", DatabaseEnvironments.Production, 4,
                [development, test, staging with { Status = "failed" }, production]) != "");
        check("Production promotion accepts one verified release across the chain",
            ReleasePromotionPolicy.BlockReason("production", DatabaseEnvironments.Production, 4,
                [development, test, staging, production]) == "");
        check("Production confirmation binds the immutable release number",
            DatabaseEnvironments.ProductionConfirmation(4) == "生产 V4");
    }

    private static DatabaseVersionStatus Status(string id, string environment, string status, int? releaseVersion)
        => new(id, id, environment, "server", "database", status, releaseVersion.HasValue ? 8 : 0,
            releaseVersion, "整库", "", "", "");
}
