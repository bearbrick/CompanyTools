using Microsoft.SqlServer.Dac;

namespace DbStudio.Core;

/// <summary>
/// 结构同步的统一部署策略。离线预览和连接数据库执行必须使用同一策略，
/// 避免提取包省略登录映射后，执行阶段意外重建数据库用户。
/// </summary>
public static class SchemaDeploymentOptions
{
    /// <summary>每次返回独立选项；Studio 只维护设计结构，不负责账号、角色和授权。</summary>
    public static DacDeployOptions Create(bool prune, bool allowDataLoss) => new()
    {
        CreateNewDatabase = false,
        BlockOnPossibleDataLoss = !allowDataLoss,
        IncludeTransactionalScripts = true,
        DropObjectsNotInSource = prune,
        DropConstraintsNotInSource = prune,
        DropIndexesNotInSource = prune,
        DropDmlTriggersNotInSource = false,
        DropExtendedPropertiesNotInSource = false,
        DropPermissionsNotInSource = false,
        DropRoleMembersNotInSource = false,
        DropStatisticsNotInSource = false,
        ScriptDatabaseOptions = false,
        ScriptDatabaseCollation = false,
        ScriptDatabaseCompatibility = false,
        ScriptFileSize = false,
        ScriptRefreshModule = false,
        DisableAndReenableDdlTriggers = false,
        DeployDatabaseInSingleUserMode = false,
        RegisterDataTierApplication = false,
        RunDeploymentPlanExecutors = false,
        IgnorePreDeployScript = true,
        IgnorePostDeployScript = true,
        IgnorePermissions = true,
        IgnoreRoleMembership = true,
        IgnoreUserSettingsObjects = true,
        IgnoreLoginSids = true,
        IgnoreAuthorizer = true,
        IgnoreFilegroupPlacement = true,
        IgnoreObjectPlacementOnPartitionScheme = true,
        IgnorePartitionSchemes = true,
        IgnoreTablePartitionOptions = true,
        DoNotAlterChangeDataCaptureObjects = true,
        DoNotAlterReplicatedObjects = true,
        IgnoreColumnOrder = true,
        CommandTimeout = 120,
        DatabaseLockTimeout = 30,
        // DoNotDrop 只保护源中缺失的对象，不能阻止同名用户因映射差异被重建。
        // 按允许类型排除其余对象，DacFx 新增对象种类也默认禁止同步。
        // 字段、索引、内联 DEFAULT 和其他约束属于 Tables；Defaults 是旧式独立默认对象。
        // Schema 没有对应枚举，由部署报告边界检查限制为建表所需的新增 schema。
        ExcludeObjectTypes = Enum.GetValues<ObjectType>()
            .Where(t => t != ObjectType.Tables && t != ObjectType.ExtendedProperties).ToArray(),
        // 现有视图、过程等对象不因项目缺少定义而被删除。
        DoNotDropObjectTypes = Enum.GetValues<ObjectType>()
            .Where(t => t != ObjectType.Tables && t != ObjectType.ExtendedProperties && t != ObjectType.Defaults).ToArray()
    };
}
