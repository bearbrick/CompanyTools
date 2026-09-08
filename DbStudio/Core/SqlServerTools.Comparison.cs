using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.SqlServer.Dac;

namespace DbStudio.Core;

/// <summary>整库与单表比对共用计划生成、授权检查和目标快照绑定。</summary>
public sealed partial class SqlServerTools
{
    /// <summary>按整个项目的已保存设计生成同步计划。</summary>
    public Task<DatabasePlan> CompareAsync(ClaimsPrincipal principal, string projectId, string connectionId,
        bool prune = false, bool allowDataLoss = false, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
        => CompareCoreAsync(principal, projectId, connectionId, prune, allowDataLoss, null, cancellationToken, progress);

    /// <summary>仅将选中表的已保存设计应用于目标模型，其他设计表不参与同步。</summary>
    public Task<DatabasePlan> CompareTableAsync(ClaimsPrincipal principal, string projectId, string connectionId, string tableId,
        bool prune = false, bool allowDataLoss = false, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(tableId)) { throw new InvalidOperationException("请选择要比对的表。"); }
        return CompareCoreAsync(principal, projectId, connectionId, prune, allowDataLoss, tableId, cancellationToken, progress);
    }
    /// <summary>基于已保存设计和目标快照生成计划，不执行变更。默认保留目标多余对象并阻止可能的数据丢失。</summary>
    private async Task<DatabasePlan> CompareCoreAsync(ClaimsPrincipal principal, string projectId, string connectionId,
        bool prune, bool allowDataLoss, string? tableId, CancellationToken cancellationToken, IProgress<string>? progress)
    {
        var credential = store.Credential(principal, projectId, connectionId);
        var project = store.DatabaseProject(principal, projectId);
        var table = tableId == null ? null : project.Tables.SingleOrDefault(item => item.Id == tableId)
            ?? throw new InvalidOperationException("请先保存当前表设计，再开始比对。");
        var scope = table == null ? null : new TableDeploymentScope(table.Id, table.Schema, table.Name);
        foreach (var expired in plans.Where(p => p.Value.View.ExpiresAt < DateTimeOffset.UtcNow))
        {
            plans.TryRemove(expired.Key, out _);
        }
        if (plans.Values.Count(p => p.UserId == principal.UserId()) >= 20)
        {
            throw new InvalidOperationException("待执行计划过多，请等待旧计划过期后重试。");
        }
        return await Task.Run(() =>
        {
            var service = new DacServices(credential.ConnectionString);
            var measurement = new ComparisonMeasurement(progress, cancellationToken);
            var targetBytes = measurement.Run("读取数据库结构与依赖", () => Extract(service, credential.Profile.Database, cancellationToken));
            var sourceBytes = measurement.Run("构建设计模型", () => scope == null ? BuildPackage(project) : TableDeployment.BuildPackage(project, scope, targetBytes));
            using var sourceStream = new MemoryStream(sourceBytes);
            using var source = DacPackage.Load(sourceStream);
            using var targetStream = new MemoryStream(targetBytes);
            using var target = DacPackage.Load(targetStream);
            var options = Options(prune, allowDataLoss);
            // 一次只读规划同时返回报告和脚本，避免两个 GenerateDeploy 调用重复计算部署计划。
            var result = measurement.Run("生成差异与同步脚本", () => DacServices.Script(source, target, credential.Profile.Database, new PublishOptions
            {
                DeployOptions = options,
                GenerateDeploymentReport = true,
                GenerateDeploymentScript = true,
                CancelToken = cancellationToken
            }));
            var xml = XDocument.Parse(result.DeploymentReport);
            var changes = SchemaDeploymentBoundary.ReadChanges(xml);
            if (changes.Count > 0)
            {
                measurement.Run(scope == null ? "核验结构同步范围" : "核验单表同步范围",
                    () => { SchemaDeploymentBoundary.Validate(sourceBytes, targetBytes, scope, changes); return true; });
            }
            var warnings = DeploymentWarnings.Parse(xml);
            var plan = new DatabasePlan(Guid.NewGuid().ToString("N"), credential.Profile.Database, project.Revision,
                DateTimeOffset.UtcNow.AddMinutes(15), changes, warnings, result.DatabaseScript, prune, allowDataLoss, scope, measurement.Timings.ToArray());
            store.RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
            plans[plan.Id] = new(plan, projectId, connectionId, credential.Profile.Revision, principal.UserId(), sourceBytes, ModelHash(targetBytes), targetBytes);
            store.LogDatabaseOperation(principal, projectId, "生成结构比对", $"{credential.Profile.Name} · {scope?.DisplayName ?? "整库"} · {changes.Count} 项差异");
            return plan;
        }, cancellationToken);
    }

}
