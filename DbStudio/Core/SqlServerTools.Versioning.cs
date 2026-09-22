using System.Xml.Linq;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace DbStudio.Core;

/// <summary>
/// 结构部署后的发布版本校验；与执行计划的风险校验保持独立。
/// </summary>
public sealed partial class SqlServerTools
{
    /// <summary>
    /// 整库发布校验的结果和可审计说明。
    /// </summary>
    private sealed record PublishVerification(bool FullyVerified, string Detail);

    /// <summary>
    /// 重新提取实际库并按完整项目生成部署报告；无待执行变更时才允许建立 V。
    /// </summary>
    private static PublishVerification VerifyPublishedStructure(
        DacServices service,
        string database,
        DesignProject project,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = Extract(service, database, cancellationToken);
            var package = BuildPackage(project, TargetCollation(target));
            using var stream = new MemoryStream(package);
            using var source = DacPackage.Load(stream);
            var report = XDocument.Parse(service.GenerateDeployReport(source, database, Options(false, false), cancellationToken));
            var verified = SchemaDeploymentBoundary.ReadChanges(report).Count == 0;
            var detail = verified
                ? "同步成功且项目管理范围内的整库结构与当前设计一致。"
                : "同步成功；项目管理范围内仍有待同步的设计差异。";
            return new(verified, detail);
        }
        catch (Exception ex) when (ex is DacServicesException or DacModelException or InvalidOperationException)
        {
            return new(false, "同步已执行，但整库发布验证未完成：" + ex.Message);
        }
    }
}
