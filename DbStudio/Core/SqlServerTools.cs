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
public record DatabasePlan(string Id, string Database, int ProjectRevision, DateTimeOffset ExpiresAt,
    List<SchemaChange> Changes, List<DeploymentWarning> Warnings, string Script, bool Prune, bool AllowDataLoss,
    TableDeploymentScope? Scope = null, IReadOnlyList<ComparisonTiming>? Timings = null);

/// <summary>
/// SQL Server 工具编排。DacFx 负责语义比对和事务部署；浏览器不能提交任意 SQL 执行。
/// 计划绑定项目、成员、连接版本和目标结构快照，执行前逐项复核。
/// </summary>
public sealed partial class SqlServerTools(StudioStore store)
{
    private sealed record StoredPlan(DatabasePlan View, string ProjectId, string ConnectionId, int ConnectionRevision,
        string UserId, byte[] Package, string TargetHash, byte[] TargetPackage);
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
    public async Task ExecuteAsync(ClaimsPrincipal principal, string projectId, string planId, string confirmedDatabase, CancellationToken cancellationToken = default)
    {
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            if (!plans.TryGetValue(planId, out var plan) || plan.ProjectId != projectId || plan.UserId != principal.UserId())
            {
                throw new InvalidOperationException("同步计划不存在或不属于当前会话，请重新比对。");
            }
            var credential = store.Credential(principal, projectId, plan.ConnectionId);
            var project = store.DatabaseProject(principal, projectId);
            if (confirmedDatabase != plan.View.Database)
            {
                throw new InvalidOperationException("请输入完整目标数据库名称确认执行。");
            }
            if (plan.View.ExpiresAt <= DateTimeOffset.UtcNow || credential.Profile.Revision != plan.ConnectionRevision || project.Revision != plan.View.ProjectRevision)
            {
                plans.TryRemove(planId, out _);
                throw new InvalidOperationException("计划已过期，或项目／连接已修改，请重新比对。");
            }
            await Task.Run(() =>
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
                // 直接面向实际连接只读规划，不能只相信省略了用户登录映射的提取快照。
                var executionReport = XDocument.Parse(service.GenerateDeployReport(source, credential.Profile.Database, options, cancellationToken));
                var executionChanges = SchemaDeploymentBoundary.ReadChanges(executionReport);
                SchemaDeploymentBoundary.Validate(executionPackage, current, plan.View.Scope, executionChanges);
                SchemaDeploymentBoundary.ValidateApproved(plan.View.Changes, executionChanges);
                if (store.DatabaseProject(principal, projectId).Revision != plan.View.ProjectRevision
                    || store.Credential(principal, projectId, plan.ConnectionId).Profile.Revision != plan.ConnectionRevision)
                {
                    plans.TryRemove(planId, out _);
                    throw new InvalidOperationException("结构检查期间项目或连接已修改，请重新比对。");
                }
                // 消耗计划后再进入不可重复的部署阶段；失败也必须重新预览。
                plans.TryRemove(planId, out _);
                var actor = store.RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
                store.LogDatabaseOperation(principal, projectId, "开始同步结构", $"{credential.Profile.Name} · 计划 {planId}");
                try
                {
                    service.Deploy(source, credential.Profile.Database, true, options, cancellationToken);
                    store.LogDatabaseResult(actor.DisplayName, projectId, "同步结构成功", credential.Profile.Name);
                }
                catch
                {
                    store.LogDatabaseResult(actor.DisplayName, projectId, "同步结构失败", $"{credential.Profile.Name} · 请检查数据库状态后重新比对");
                    throw;
                }
            }, cancellationToken);
        }
        finally { executionGate.Release(); }
    }

    /// <summary>构建纯结构包：先声明非 dbo 架构，表与外键由 DacFx 统一解析依赖。</summary>
    public static byte[] BuildPackage(DesignProject project)
    {
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
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
}
