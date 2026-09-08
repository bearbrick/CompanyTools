using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.SqlServer.Dac;

namespace DbStudio.Core;

/// <summary>整库与单表比对共用计划生成、授权检查和目标快照绑定。</summary>
public sealed partial class SqlServerTools
{
    /// <summary>按整个项目的已保存设计生成同步计划。</summary>
    public Task<DatabasePlan> CompareAsync(ClaimsPrincipal principal, string projectId, string connectionId,
        bool prune = false, bool allowDataLoss = false, CancellationToken cancellationToken = default)
        => CompareCoreAsync(principal, projectId, connectionId, prune, allowDataLoss, null, cancellationToken);

    /// <summary>仅将选中表的已保存设计应用于目标模型，其他设计表不参与同步。</summary>
    public Task<DatabasePlan> CompareTableAsync(ClaimsPrincipal principal, string projectId, string connectionId, string tableId,
        bool prune = false, bool allowDataLoss = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableId)) { throw new InvalidOperationException("请选择要比对的表。"); }
        return CompareCoreAsync(principal, projectId, connectionId, prune, allowDataLoss, tableId, cancellationToken);
    }
    /// <summary>基于已保存设计和目标快照生成计划，不执行变更。默认保留目标多余对象并阻止可能的数据丢失。</summary>
    private async Task<DatabasePlan> CompareCoreAsync(ClaimsPrincipal principal, string projectId, string connectionId,
        bool prune, bool allowDataLoss, string? tableId, CancellationToken cancellationToken)
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
            var targetBytes = Extract(service, credential.Profile.Database, cancellationToken);
            var sourceBytes = scope == null ? BuildPackage(project) : TableDeployment.BuildPackage(project, scope, targetBytes);
            using var sourceStream = new MemoryStream(sourceBytes);
            using var source = DacPackage.Load(sourceStream);
            using var targetStream = new MemoryStream(targetBytes);
            using var target = DacPackage.Load(targetStream);
            var options = Options(prune, allowDataLoss);
            var report = DacServices.GenerateDeployReport(source, target, credential.Profile.Database, options);
            var script = DacServices.GenerateDeployScript(source, target, credential.Profile.Database, options);
            var xml = XDocument.Parse(report);
            var changes = xml.Descendants().Where(e => e.Name.LocalName == "Operation")
                .SelectMany(operation => operation.Descendants().Where(e => e.Name.LocalName == "Item")
                    .Select(item => new SchemaChange((string?)operation.Attribute("Name") ?? "", (string?)item.Attribute("Type") ?? "", (string?)item.Attribute("Value") ?? ""))).ToList();
            if (scope != null) { TableDeployment.ValidateChanges(sourceBytes, targetBytes, scope, changes); }
            var warnings = DeploymentWarnings.Parse(xml);
            var plan = new DatabasePlan(Guid.NewGuid().ToString("N"), credential.Profile.Database, project.Revision,
                DateTimeOffset.UtcNow.AddMinutes(15), changes, warnings, script, prune, allowDataLoss, scope);
            store.RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
            plans[plan.Id] = new(plan, projectId, connectionId, credential.Profile.Revision, principal.UserId(), sourceBytes, ModelHash(targetBytes));
            store.LogDatabaseOperation(principal, projectId, "生成结构比对", $"{credential.Profile.Name} · {scope?.DisplayName ?? "整库"} · {changes.Count} 项差异");
            return plan;
        }, cancellationToken);
    }

}
