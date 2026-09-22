using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace DbStudio.Core;

/// <summary>可审阅的结构变更项。</summary>
public record SchemaChange(string Operation, string ObjectType, string Name);

/// <summary>只包含可显示信息的同步计划，实际执行始终使用服务端保存的不可变包。</summary>
public record DatabasePlan(string Id, string Database, int ProjectRevision, int? ReleaseVersion, string Environment, DateTimeOffset ExpiresAt,
    List<SchemaChange> Changes, List<DeploymentWarning> Warnings, List<DataLossRisk> DataLossRisks, string Script, bool Prune, bool AllowDataLoss,
    TableDeploymentScope? Scope = null, IReadOnlyList<ComparisonTiming>? Timings = null);

/// <summary>
/// SQL Server 工具编排。DacFx 负责语义比对和事务部署；浏览器不能提交任意 SQL 执行。
/// 计划绑定项目、成员、连接版本和目标结构快照，执行前逐项复核。
/// </summary>
public sealed partial class SqlServerTools(StudioStore store)
{
    private sealed record StoredPlan(DatabasePlan View, string ProjectId, string ConnectionId, int ConnectionRevision,
        string UserId, DesignProject SourceProject, byte[] Package, string TargetHash, byte[] TargetPackage,
        IReadOnlyList<EmptyColumnDeletion.Column> EmptyColumns);
    private readonly ConcurrentDictionary<string, StoredPlan> plans = new();
    private readonly SemaphoreSlim executionGate = new(1, 1);

    /// <summary>测试已保存的连接，仅查询数据库名和服务器版本。</summary>
    public async Task<string> TestAsync(ClaimsPrincipal principal, string projectId, string connectionId, CancellationToken cancellationToken = default)
    {
        var credential = store.Credential(principal, projectId, connectionId);
        using var connection = new SqlConnection(credential.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CONCAT(DB_NAME(),N' · SQL Server ',CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')))";
        var result = Convert.ToString(await cmd.ExecuteScalarAsync(cancellationToken))!;
        store.RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
        return result;
    }

    /// <summary>从已授权连接读取结构；完成读取后再次检查成员权限。</summary>
    public async Task<DatabaseSnapshot> ReverseAsync(ClaimsPrincipal principal, string projectId, string connectionId, CancellationToken cancellationToken = default)
    {
        var credential = store.Credential(principal, projectId, connectionId);
        var snapshot = await SqlServerCatalog.ReadAsync(credential.ConnectionString, cancellationToken);
        store.RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
        return snapshot;
    }

    /// <summary>
    /// 执行服务端计划。必须提交精确数据库名；拒绝过期、跨用户、连接变更、项目变更、目标结构变更和重复提交。
    /// DacFx 再次验证依赖和数据丢失条件，事务提交失败时由部署引擎回滚。
    /// </summary>
    public async Task<DatabaseDeploymentResult> ExecuteAsync(ClaimsPrincipal principal, string projectId, string planId, string confirmedDatabase, CancellationToken cancellationToken = default)
        => await ExecuteAsync(principal, projectId, planId, confirmedDatabase, "", "", cancellationToken);

    /// <summary>
    /// 执行服务端计划；生产环境必须额外提交与计划 V 对应的确认短语。
    /// </summary>
    public async Task<DatabaseDeploymentResult> ExecuteAsync(ClaimsPrincipal principal, string projectId, string planId,
        string confirmedDatabase, string productionConfirmation, CancellationToken cancellationToken = default)
        => await ExecuteAsync(principal, projectId, planId, confirmedDatabase, productionConfirmation, "", cancellationToken);

    /// <summary>
    /// 执行服务端计划；生产发布和数据损失分别使用独立、精确匹配的确认短语。
    /// </summary>
    public async Task<DatabaseDeploymentResult> ExecuteAsync(ClaimsPrincipal principal, string projectId, string planId,
        string confirmedDatabase, string productionConfirmation, string dataLossConfirmation, CancellationToken cancellationToken = default)
    {
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            if (!plans.TryGetValue(planId, out var plan) || plan.ProjectId != projectId || plan.UserId != principal.UserId())
            {
                throw new InvalidOperationException("同步计划不存在或不属于当前会话，请重新比对。");
            }
            var credential = store.Credential(principal, projectId, plan.ConnectionId);
            var currentProject = store.DatabaseProject(principal, projectId);
            var project = plan.SourceProject;
            if (confirmedDatabase != plan.View.Database)
            {
                throw new InvalidOperationException("请输入完整目标数据库名称确认执行。");
            }
            if (plan.View.Environment == DatabaseEnvironments.Production
                && (plan.View.ReleaseVersion is not int productionRelease
                    || productionConfirmation != DatabaseEnvironments.ProductionConfirmation(productionRelease)))
            {
                throw new InvalidOperationException("生产发布确认短语不正确，请重新核对发布版本。");
            }
            if (plan.View.DataLossRisks.Count > 0 && !plan.View.AllowDataLoss)
            {
                throw new InvalidOperationException("计划包含可能丢失数据的变更。请返回比对选项启用高风险 DDL，重新生成计划并逐项核对。");
            }
            if (plan.View.DataLossRisks.Count > 0
                && dataLossConfirmation != DataLossAssessment.Confirmation(plan.View.Database))
            {
                throw new InvalidOperationException("数据损失确认短语不正确，请核对高风险变更和目标数据库。");
            }
            if (plan.View.ExpiresAt <= DateTimeOffset.UtcNow || credential.Profile.Revision != plan.ConnectionRevision
                || (plan.View.ReleaseVersion == null && currentProject.Revision != plan.View.ProjectRevision))
            {
                plans.TryRemove(planId, out _);
                throw new InvalidOperationException("计划已过期，或项目／连接已修改，请重新比对。");
            }
            if (plan.View.ReleaseVersion is int releaseVersion)
            {
                store.EnsurePromotionAllowed(principal, projectId, plan.ConnectionId, releaseVersion);
                project = store.DeploymentProject(principal, projectId, releaseVersion);
            }
            return await Task.Run(() =>
            {
                var service = new DacServices(credential.ConnectionString);
                var current = Extract(service, credential.Profile.Database, cancellationToken);
                var snapshotChanged = ModelHash(current) != plan.TargetHash;
                if (snapshotChanged)
                {
                    var drift = SchemaDriftCheck.FindChanges(plan.TargetPackage, current, plan.View.Database, project, plan.View.Scope, cancellationToken);
                    if (drift.Count > 0)
                    {
                        plans.TryRemove(planId, out _);
                        throw new InvalidOperationException((plan.View.Scope == null ? "数据库中的表结构已变化" : "当前表或关联表的结构已变化")
                            + "，请重新比对后再执行：\n" + string.Join("\n", drift.Take(8).Select(change => $"{change.Operation} · {change.ObjectType} · {change.Name}")));
                    }
                }
                // 无关表有变化时以当前快照重新叠加同一份已保存设计，不能把旧快照中其他表的结构写回。
                var executionPackage = snapshotChanged && plan.View.Scope != null
                    ? TableDeployment.BuildPackage(project, plan.View.Scope, current) : plan.Package;
                using var sourceStream = new MemoryStream(executionPackage);
                using var source = DacPackage.Load(sourceStream);
                var options = Options(plan.View.Prune, plan.View.AllowDataLoss);
                if (!plan.View.AllowDataLoss && SchemaTypeExpansion.Assess(executionPackage, current, plan.View.Database, plan.View.Prune, cancellationToken).Count > 0)
                {
                    options.BlockOnPossibleDataLoss = false;
                }
                // 直接面向实际连接只读规划，不能只相信省略了用户登录映射的提取快照。
                var executionReport = XDocument.Parse(service.GenerateDeployReport(source, credential.Profile.Database, options, cancellationToken));
                var executionChanges = SchemaDeploymentBoundary.ReadChanges(executionReport);
                SchemaDeploymentBoundary.Validate(executionPackage, current, plan.View.Scope, executionChanges);
                SchemaDeploymentBoundary.ValidateApproved(plan.View.Changes, executionChanges);
                if (plan.EmptyColumns.Count > 0)
                {
                    var deletions = EmptyColumnDeletion.Assess(executionPackage, current, plan.View.Database, plan.View.Prune, cancellationToken);
                    if (!deletions.SequenceEqual(plan.EmptyColumns))
                    {
                        throw new InvalidOperationException("空列删除计划已变化，请重新比对。");
                    }
                }
                if ((plan.View.ReleaseVersion == null && store.DatabaseProject(principal, projectId).Revision != plan.View.ProjectRevision)
                    || store.Credential(principal, projectId, plan.ConnectionId).Profile.Revision != plan.ConnectionRevision)
                {
                    plans.TryRemove(planId, out _);
                    throw new InvalidOperationException("结构检查期间项目或连接已修改，请重新比对。");
                }
                if (plan.View.ReleaseVersion is int verifiedRelease)
                {
                    // DacFx 规划可能耗时较长，进入不可重复部署前再次检查低级环境状态。
                    store.EnsurePromotionAllowed(principal, projectId, plan.ConnectionId, verifiedRelease);
                }
                // 消耗计划后再进入不可重复的部署阶段；失败也必须重新预览。
                plans.TryRemove(planId, out _);
                var actor = store.RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
                store.LogDatabaseOperation(principal, projectId, "开始同步结构", $"{credential.Profile.Name} · 计划 {planId}");
                var deploymentId = store.BeginDeployment(actor.DisplayName, projectId, credential.Profile, project.Revision,
                    plan.View.ReleaseVersion, plan.View.Scope?.DisplayName ?? "整库");
                try
                {
                    if (plan.EmptyColumns.Count > 0)
                    {
                        EmptyColumnDeletion.Execute(credential.ConnectionString, plan.EmptyColumns, cancellationToken);
                    }
                    else
                    {
                        service.Deploy(source, credential.Profile.Database, true, options, cancellationToken);
                    }
                    var verification = VerifyPublishedStructure(service, credential.Profile.Database, project, cancellationToken);
                    var result = store.CompleteDeployment(deploymentId, project, actor.DisplayName, plan.View.Environment,
                        plan.View.ReleaseVersion, verification.FullyVerified, verification.Detail);
                    store.LogDatabaseResult(actor.DisplayName, projectId, verification.FullyVerified ? "发布数据库版本" : "局部同步结构", $"{credential.Profile.Name} · {result.Message}");
                    return result;
                }
                catch (Exception ex)
                {
                    store.FailDeployment(deploymentId, "同步失败：" + ex.Message);
                    store.LogDatabaseResult(actor.DisplayName, projectId, "同步结构失败", $"{credential.Profile.Name} · 请检查数据库状态后重新比对");
                    throw;
                }
            }, cancellationToken);
        }
        finally { executionGate.Release(); }
    }

    /// <summary>构建纯结构包：先声明非 dbo 架构，表与外键由 DacFx 统一解析依赖。</summary>
    public static byte[] BuildPackage(DesignProject project, string? collation = null)
    {
        var modelOptions = new TSqlModelOptions();
        if (!string.IsNullOrWhiteSpace(collation))
        {
            modelOptions.Collation = collation;
        }
        using var model = new TSqlModel(SqlServerVersion.Sql160, modelOptions);
        var script = new StringBuilder();
        foreach (var schema in project.Tables.Select(t => t.Schema).Distinct(StringComparer.OrdinalIgnoreCase).Where(s => !s.Equals("dbo", StringComparison.OrdinalIgnoreCase)))
        {
            script.AppendLine($"CREATE SCHEMA {SqlServerDdl.Q(schema)};\nGO");
        }
        foreach (var table in project.Tables)
        {
            script.AppendLine(SqlServerDdl.Generate(project, table, forModel: true));
            script.AppendLine("GO");
        }
        model.AddObjects(script.ToString());
        using var stream = new MemoryStream();
        DacPackageExtensions.BuildPackage(stream, model, new PackageMetadata { Name = "DbStudioDesign", Version = "1.0.0.0" });
        return stream.ToArray();
    }

    private static byte[] Extract(DacServices service, string database, CancellationToken token)
    {
        using var stream = new MemoryStream();
        service.Extract(stream, database, "DbStudioTarget", new Version(1, 0), null, null, new DacExtractOptions
        {
            ExtractAllTableData = false,
            IgnorePermissions = true,
            IgnoreUserLoginMappings = true,
            ExtractApplicationScopedObjectsOnly = true,
            VerifyExtraction = true
        }, token);
        return stream.ToArray();
    }

    /// <summary>使用稳定结构指纹，避免 DacFx 每次提取的登录密码占位值造成误报。</summary>
    private static string ModelHash(byte[] package) => SchemaModelFingerprint.Create(package);

    private static DacDeployOptions Options(bool prune, bool allowDataLoss)
        => SchemaDeploymentOptions.Create(prune, allowDataLoss);

    /// <summary>设计中的默认排序规则继承目标库；不通过部署修改目标数据库排序规则。</summary>
    private static string? TargetCollation(byte[] package)
    {
        using var input = new MemoryStream(package);
        using var model = TSqlModel.LoadFromDacpac(input, new ModelLoadOptions());
        return model.GetObjects(DacQueryScopes.All, DatabaseOptions.TypeClass).FirstOrDefault()?.GetProperty<string>(DatabaseOptions.Collation);
    }
}
