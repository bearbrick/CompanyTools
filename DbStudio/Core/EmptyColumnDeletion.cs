using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbStudio.Core;

/// <summary>只处理没有其他结构变动的普通列删除；检查数据和删除使用同一事务与排他表锁。</summary>
public static class EmptyColumnDeletion
{
    /// <summary>经模型比对确认待删除的普通列。</summary>
    public sealed record Column
    {
        /// <summary>创建一个待删除列标识。</summary>
        public Column(string schema, string table, string name)
        {
            Schema = schema;
            Table = table;
            Name = name;
        }

        /// <summary>所属数据库架构。</summary>
        public string Schema
        {
            get;
        }
        /// <summary>所属表名。</summary>
        public string Table
        {
            get;
        }
        /// <summary>列名。</summary>
        public string Name
        {
            get;
        }
        /// <summary>已安全引用的架构和表名。</summary>
        public string TableSql => $"{SqlServerDdl.Q(Schema)}.{SqlServerDdl.Q(Table)}";
        /// <summary>用于预览和审计的完整列名。</summary>
        public string DisplayName => $"{TableSql}.{SqlServerDdl.Q(Name)}";
    }

    /// <summary>在内存中补回被删列；只有剩余比对为零才接受，混合变更不能搭便车放行。</summary>
    public static List<Column> Assess(byte[] sourceBytes, byte[] targetBytes, string database, bool prune, CancellationToken token, List<string>? reasons = null)
    {
        // DropObjectsNotInSource 不控制所有表内删列；必须按模型中的实际删列核验，不能用 prune 跳过。
        using var sourceStream = new MemoryStream(sourceBytes);
        using var source = TSqlModel.LoadFromDacpac(sourceStream, new ModelLoadOptions { LoadAsScriptBackedModel = true });
        using var targetStream = new MemoryStream(targetBytes);
        using var target = TSqlModel.LoadFromDacpac(targetStream, new ModelLoadOptions());
        var targets = target.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass).ToDictionary(t => t.Name.ToString(), StringComparer.Ordinal);
        var removed = new List<Column>();
        foreach (var table in source.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass).ToList())
        {
            token.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(table.Name.ToString(), out var oldTable))
            {
                continue;
            }
            var before = Definition(oldTable.GetScript());
            var after = Definition(table.GetScript());
            if (before == null || after == null)
            {
                reasons?.Add($"{table.Name}：无法完整解析表定义，未自动放行空列删除。");
                return [];
            }
            var names = after.Definition.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToHashSet(StringComparer.Ordinal);
            var missing = before.Definition.ColumnDefinitions.Where(c => !names.Contains(c.ColumnIdentifier.Value)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }
            foreach (var column in missing)
            {
                // 默认值、主键、自增、计算列及特殊列交给常规部署，不能隐式移除依赖约束。
                if (column.DataType is not SqlDataTypeReference || column.ComputedColumnExpression != null
                    || column.IdentityOptions != null || column.DefaultConstraint != null || column.Constraints.Any(c => c is not NullableConstraintDefinition)
                    || column.GeneratedAlways != null)
                {
                    reasons?.Add($"{table.Name}.{SqlServerDdl.Q(column.ColumnIdentifier.Value)}：包含默认约束、自增、计算或特殊属性，不能按普通空列直接删除。");
                    return [];
                }
                after.Definition.ColumnDefinitions.Add(column);
                removed.Add(new(table.Name.Parts[0], table.Name.Parts[1], column.ColumnIdentifier.Value));
            }
            source.AddOrUpdateObjects(Sql(after), table.GetSourceInformation().SourceName, new TSqlObjectOptions());
        }
        if (removed.Count == 0)
        {
            return [];
        }
        // 删除列时其扩展属性随列消失；补回属性后再确认没有其他说明变更。
        foreach (var property in target.GetObjects(DacQueryScopes.UserDefined, ExtendedProperty.TypeClass))
        {
            if (property.GetReferenced(ExtendedProperty.Host).Any(host => removed.Any(c => host.Name.ToString() == c.DisplayName)))
            {
                source.AddObjects(property.GetScript());
            }
        }
        using var normalized = new MemoryStream();
        DacPackageExtensions.BuildPackage(normalized, source, new PackageMetadata { Name = "EmptyColumnAssessment" });
        normalized.Position = 0;
        using var normalizedPackage = DacPackage.Load(normalized);
        using var targetPackageStream = new MemoryStream(targetBytes);
        using var targetPackage = DacPackage.Load(targetPackageStream);
        var report = XDocument.Parse(DacServices.Script(normalizedPackage, targetPackage, database, new PublishOptions
        {
            DeployOptions = SchemaDeploymentOptions.Create(prune, false),
            GenerateDeploymentReport = true,
            GenerateDeploymentScript = false,
            CancelToken = token
        }).DeploymentReport);
        var remaining = SchemaDeploymentBoundary.ReadChanges(report);
        if (remaining.Count > 0)
        {
            reasons?.Add("本次计划除删除列以外还有其他差异，未自动放行：" + string.Join("；", remaining.Take(8).Select(c => $"{c.Operation} {c.ObjectType} {c.Name}")));
            return [];
        }
        return removed.OrderBy(c => c.DisplayName, StringComparer.Ordinal).ToList();
    }

    /// <summary>只查询是否有非 NULL 值，不读取业务值。无法完整查看安全策略时不自动放行。</summary>
    public static bool AreEmpty(string connectionString, IReadOnlyList<Column> columns, CancellationToken token, List<string>? reasons = null)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = Guards(columns) + string.Join("\n", columns.Select(c =>
            $"IF EXISTS (SELECT 1 FROM {c.TableSql} WHERE {SqlServerDdl.Q(c.Name)} IS NOT NULL) SELECT N'{c.DisplayName.Replace("'", "''")}';"));
        using var registration = token.Register(command.Cancel);
        token.ThrowIfCancellationRequested();
        try
        {
            var populated = command.ExecuteScalar();
            if (populated == null)
            {
                return true;
            }
            reasons?.Add($"{populated}：存在非 NULL 数据（空字符串、空格和 0 也算数据），未自动放行删除。");
            return false;
        }
        catch (SqlException ex) when (!token.IsCancellationRequested)
        {
            reasons?.Add(ex.Number == 51000 ? ex.Message : $"无法查询空列数据（SQL {ex.Number}），未自动放行。请检查查询权限和数据库版本；数据为空尚未得到确认。");
            return false;
        }
    }

    // 全库 VIEW DEFINITION 用于可靠确认 RLS 不会隐藏行；存在任何行安全策略时保守禁用此捷径。
    private const string AccessGuard = """
        IF ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0) <> 1
            THROW 51000, N'当前连接缺少数据库 VIEW DEFINITION 权限，无法完整核验空列删除条件，未自动放行。', 1;
        IF EXISTS (SELECT 1 FROM sys.security_predicates)
            THROW 51000, N'数据库存在行安全策略，查询可能隐藏数据，未自动放行空列删除。', 1;

        """;

    /// <summary>服务端生成固定用途脚本；先锁定并检查所有列，再删除。任何失败都会回滚。</summary>
    public static string Script(IReadOnlyList<Column> columns)
    {
        var sql = new StringBuilder("SET XACT_ABORT ON;\nSET LOCK_TIMEOUT 30000;\nBEGIN TRY\nBEGIN TRANSACTION;\n").Append(Guards(columns));
        foreach (var column in columns)
        {
            var message = (column.DisplayName + " 已包含非 NULL 数据，已停止删除，请重新比对。").Replace("'", "''");
            sql.AppendLine($"IF EXISTS (SELECT 1 FROM {column.TableSql} WITH (TABLOCKX, HOLDLOCK) WHERE {SqlServerDdl.Q(column.Name)} IS NOT NULL)")
                .AppendLine($"    THROW 51000, N'{message}', 1;");
        }
        // 拿到表锁后再次核验，避免首次检查与锁定之间新增的行安全策略隐藏数据。
        sql.Append(Guards(columns));
        foreach (var column in columns)
        {
            sql.AppendLine($"ALTER TABLE {column.TableSql} DROP COLUMN {SqlServerDdl.Q(column.Name)};");
        }
        return sql.AppendLine("COMMIT TRANSACTION;\nEND TRY\nBEGIN CATCH\nIF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\nTHROW;\nEND CATCH;").ToString();
    }

    /// <summary>在单一数据库事务中锁定、复查并删除已批准的空列。</summary>
    public static void Execute(string connectionString, IReadOnlyList<Column> columns, CancellationToken token)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = Script(columns);
        using var registration = token.Register(command.Cancel);
        token.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
    }

    private static string Guards(IReadOnlyList<Column> columns) => AccessGuard + string.Join("\n", columns.Select(c => c.TableSql).Distinct(StringComparer.Ordinal).Select(table =>
        $"IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id=OBJECT_ID(N'{table.Replace("'", "''")}') AND (temporal_type<>0 OR is_memory_optimized=1 OR is_tracked_by_cdc=1 OR is_replicated=1 OR is_merge_published=1)) THROW 51000, N'特殊表必须使用常规部署检查，不自动删除空列。', 1;\n"));

    private static string Sql(TSqlFragment fragment)
    {
        new Sql160ScriptGenerator().GenerateScript(fragment, out var sql);
        return sql;
    }
    private static CreateTableStatement? Definition(string sql)
    {
        var fragment = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        return errors.Count == 0 && fragment is TSqlScript script
            ? script.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().SingleOrDefault() : null;
    }
}
