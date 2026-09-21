using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DbStudio.Core;

public sealed partial class SqlServerTools
{
    private sealed record StoredCleanupPlan(DataCleanupPlan View, string ProjectId, string ConnectionId,
        int ConnectionRevision, string UserId, string TargetSignature);
    private sealed record ActualCleanupTable(int ObjectId, TableDesign Design, decimal? Seed, decimal? Increment);
    private sealed record ActualForeignKey(string Name, int ParentObjectId, int TargetObjectId,
        string ParentSchema, string ParentTable, string TargetSchema, string TargetTable,
        IReadOnlyList<string> ParentColumns, IReadOnlyList<string> TargetColumns,
        byte DeleteAction, byte UpdateAction, bool NotForReplication, bool Disabled, bool NotTrusted);

    private readonly ConcurrentDictionary<string, StoredCleanupPlan> cleanupPlans = new();

    /// <summary>
    /// 按稳定表 ID 创建清空计划。先从已保存设计确定范围，再读取实际数据库核验表与外键；
    /// 浏览器不能提交表名或任意 SQL。
    /// </summary>
    public async Task<DataCleanupPlan> PlanCleanupAsync(ClaimsPrincipal principal, string projectId, string connectionId,
        IReadOnlyCollection<string> tableIds, CancellationToken cancellationToken = default)
    {
        var credential = store.Credential(principal, projectId, connectionId);
        var project = store.DatabaseProject(principal, projectId);
        var ids = tableIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            throw new InvalidOperationException("当前范围没有可清空的数据表。请先为表设置分类，或选择单表操作。");
        }
        if (ids.Any(id => project.Tables.All(table => table.Id != id)))
        {
            throw new InvalidOperationException("清空范围包含已删除或不属于当前项目的表，请刷新后重试。");
        }
        var selected = project.Tables.Where(table => ids.Contains(table.Id, StringComparer.Ordinal)).ToList();

        await using var connection = new SqlConnection(credential.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (!string.Equals(connection.Database, credential.Profile.Database, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("实际连接的数据库与保存的目标名称不一致，已停止生成清空计划。");
        }
        var actual = await ReadCleanupTablesAsync(connection, selected, cancellationToken);
        var actualIds = actual.Select(table => table.ObjectId).ToHashSet();
        var foreignKeys = await ReadForeignKeysAsync(connection, actualIds, cancellationToken);
        EnsureNoExternalIncomingReferences(actualIds, foreignKeys);

        var script = BuildCleanupScript(credential.Profile.Database, actual, foreignKeys);
        var view = new DataCleanupPlan(Guid.NewGuid().ToString("N"), credential.Profile.Database, project.Revision,
            DateTimeOffset.UtcNow.AddMinutes(15), actual.Select(table => new DataCleanupTable(table.Design.Id,
                table.Design.Schema, table.Design.Name, table.Design.Label,
                TableDataCategories.Get(table.Design.DataCategory).Name, table.Seed.HasValue)).ToList(), script);
        cleanupPlans[view.Id] = new(view, projectId, connectionId, credential.Profile.Revision, principal.UserId(),
            CleanupSignature(actual, foreignKeys));
        foreach (var expired in cleanupPlans.Where(pair => pair.Value.View.ExpiresAt <= DateTimeOffset.UtcNow).Select(pair => pair.Key))
        {
            cleanupPlans.TryRemove(expired, out _);
        }
        return view;
    }

    /// <summary>消费一次性计划并执行冻结脚本；脚本自身仍检查库名、表存在性和范围外引用。</summary>
    public async Task ExecuteCleanupAsync(ClaimsPrincipal principal, string projectId, string planId,
        string confirmedDatabase, CancellationToken cancellationToken = default)
    {
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            if (!cleanupPlans.TryGetValue(planId, out var stored) || stored.ProjectId != projectId
                || stored.UserId != principal.UserId())
            {
                throw new InvalidOperationException("清空计划不存在或不属于当前会话，请重新生成。");
            }
            var credential = store.Credential(principal, projectId, stored.ConnectionId);
            var project = store.DatabaseProject(principal, projectId);
            if (confirmedDatabase != stored.View.Database)
            {
                throw new InvalidOperationException("请输入完整目标数据库名称确认清空操作。");
            }
            if (stored.View.ExpiresAt <= DateTimeOffset.UtcNow || project.Revision != stored.View.ProjectRevision
                || credential.Profile.Revision != stored.ConnectionRevision)
            {
                cleanupPlans.TryRemove(planId, out _);
                throw new InvalidOperationException("清空计划已过期，或项目／连接已修改，请重新生成并核对。");
            }

            var actor = store.RequireProject(principal, projectId, ProjectAccess.Database, Permission.Design);
            try
            {
                await using var connection = new SqlConnection(credential.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                if (!string.Equals(connection.Database, stored.View.Database, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("实际连接的数据库与清空计划不一致，请重新生成计划。");
                }
                var selectedIds = stored.View.Tables.Select(table => table.TableId).ToHashSet(StringComparer.Ordinal);
                var selected = project.Tables.Where(table => selectedIds.Contains(table.Id)).ToList();
                var actual = await ReadCleanupTablesAsync(connection, selected, cancellationToken);
                var objectIds = actual.Select(table => table.ObjectId).ToHashSet();
                var foreignKeys = await ReadForeignKeysAsync(connection, objectIds, cancellationToken);
                EnsureNoExternalIncomingReferences(objectIds, foreignKeys);
                if (!string.Equals(CleanupSignature(actual, foreignKeys), stored.TargetSignature, StringComparison.Ordinal))
                {
                    cleanupPlans.TryRemove(planId, out _);
                    throw new InvalidOperationException("目标表或外键结构在生成计划后已变化，请重新生成并核对清空计划。");
                }

                // 在不可重复的写入前消费计划；失败后也必须重新读取目标状态并预览。
                cleanupPlans.TryRemove(planId, out _);
                store.LogDatabaseOperation(principal, projectId, "开始清空数据",
                    $"{credential.Profile.Name} · {stored.View.Tables.Count} 张表 · TRUNCATE 计划 {planId}");
                await using var command = connection.CreateCommand();
                command.CommandText = stored.View.Script;
                command.CommandTimeout = 0;
                await command.ExecuteNonQueryAsync(cancellationToken);
                store.LogDatabaseResult(actor.DisplayName, projectId, "清空数据成功",
                    $"{credential.Profile.Name} · {stored.View.Tables.Count} 张表");
            }
            catch
            {
                store.LogDatabaseResult(actor.DisplayName, projectId, "清空数据失败",
                    $"{credential.Profile.Name} · 事务已请求回滚，请重新检查目标数据库");
                throw;
            }
        }
        finally
        {
            executionGate.Release();
        }
    }

    private static async Task<List<ActualCleanupTable>> ReadCleanupTablesAsync(SqlConnection connection,
        IReadOnlyList<TableDesign> selected, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        var predicates = new List<string>();
        for (var i = 0; i < selected.Count; i++)
        {
            predicates.Add($"(s.name=@schema{i} AND t.name=@table{i})");
            command.Parameters.AddWithValue($"@schema{i}", selected[i].Schema);
            command.Parameters.AddWithValue($"@table{i}", selected[i].Name);
        }
        command.CommandText = $"""
            SELECT t.object_id,s.name,t.name,CONVERT(decimal(38,0),ic.seed_value),CONVERT(decimal(38,0),ic.increment_value)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id=t.object_id
            WHERE {string.Join(" OR ", predicates)}
            ORDER BY t.object_id
            """;
        var rows = new List<(int Id, string Schema, string Name, decimal? Seed, decimal? Increment)>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDecimal(3), reader.IsDBNull(4) ? null : reader.GetDecimal(4)));
            }
        }
        var missing = selected.Where(table => !rows.Any(row => row.Schema.Equals(table.Schema, StringComparison.OrdinalIgnoreCase)
            && row.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("目标数据库缺少以下设计表，请先同步结构或调整范围：\n"
                + string.Join("\n", missing.Select(table => $"{table.Schema}.{table.Name}")));
        }
        return rows.Select(row => new ActualCleanupTable(row.Id, selected.Single(table =>
            table.Schema.Equals(row.Schema, StringComparison.OrdinalIgnoreCase)
            && table.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)), row.Seed, row.Increment)).ToList();
    }

    private static async Task<List<ActualForeignKey>> ReadForeignKeysAsync(SqlConnection connection,
        IReadOnlySet<int> selectedIds, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        var parameters = selectedIds.Select((id, index) => (id, name: $"@id{index}")).ToList();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.name, parameter.id);
        }
        var list = string.Join(',', parameters.Select(parameter => parameter.name));
        command.CommandText = $"""
            SELECT fk.object_id,fk.name,fk.parent_object_id,fk.referenced_object_id,
                   ps.name,pt.name,rs.name,rt.name,
                   fk.delete_referential_action,fk.update_referential_action,
                   fk.is_not_for_replication,fk.is_disabled,fk.is_not_trusted,
                   fkc.constraint_column_id,pc.name,rc.name
            FROM sys.foreign_keys fk
            JOIN sys.tables pt ON pt.object_id=fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id=pt.schema_id
            JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
            JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id
            WHERE fk.parent_object_id IN ({list}) OR fk.referenced_object_id IN ({list})
            ORDER BY fk.object_id,fkc.constraint_column_id
            """;
        var rows = new List<(int Id, string Name, int ParentId, int TargetId, string ParentSchema,
            string ParentTable, string TargetSchema, string TargetTable, byte DeleteAction, byte UpdateAction,
            bool NotForReplication, bool Disabled, bool NotTrusted, int Position, string ParentColumn, string TargetColumn)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetByte(8), reader.GetByte(9), reader.GetBoolean(10), reader.GetBoolean(11), reader.GetBoolean(12),
                reader.GetInt32(13), reader.GetString(14), reader.GetString(15)));
        }
        return rows.GroupBy(row => row.Id).Select(group =>
        {
            var first = group.First();
            var ordered = group.OrderBy(row => row.Position).ToList();
            return new ActualForeignKey(first.Name, first.ParentId, first.TargetId, first.ParentSchema,
                first.ParentTable, first.TargetSchema, first.TargetTable,
                ordered.Select(row => row.ParentColumn).ToList(), ordered.Select(row => row.TargetColumn).ToList(),
                first.DeleteAction, first.UpdateAction, first.NotForReplication, first.Disabled, first.NotTrusted);
        }).ToList();
    }

    private static void EnsureNoExternalIncomingReferences(IReadOnlySet<int> actualIds,
        IReadOnlyList<ActualForeignKey> foreignKeys)
    {
        var blockers = foreignKeys.Where(key => actualIds.Contains(key.TargetObjectId)
            && !actualIds.Contains(key.ParentObjectId)).ToList();
        if (blockers.Count == 0)
        {
            return;
        }
        var detail = string.Join("\n", blockers.Take(12).Select(key => $"{key.ParentSchema}.{key.ParentTable} / {key.Name}"));
        throw new InvalidOperationException("所选表仍被范围外的数据表引用，不能安全执行 TRUNCATE。请调整分类使关联子表一并纳入：\n" + detail);
    }

    private static string CleanupSignature(IReadOnlyList<ActualCleanupTable> tables,
        IReadOnlyList<ActualForeignKey> foreignKeys)
    {
        var value = new StringBuilder();
        foreach (var table in tables.OrderBy(table => table.ObjectId))
        {
            value.AppendLine($"T|{table.ObjectId}|{table.Design.Schema}|{table.Design.Name}|{table.Seed}|{table.Increment}");
        }
        foreach (var key in foreignKeys.OrderBy(key => key.ParentObjectId).ThenBy(key => key.Name, StringComparer.Ordinal))
        {
            value.AppendLine($"F|{key.ParentObjectId}|{key.TargetObjectId}|{key.Name}|{key.DeleteAction}|{key.UpdateAction}|{key.NotForReplication}|{key.Disabled}|{key.NotTrusted}|{string.Join(',', key.ParentColumns)}|{string.Join(',', key.TargetColumns)}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static string BuildCleanupScript(string database, IReadOnlyList<ActualCleanupTable> tables,
        IReadOnlyList<ActualForeignKey> foreignKeys)
    {
        static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
        static string Qualified(TableDesign table) => $"{SqlServerDdl.Q(table.Schema)}.{SqlServerDdl.Q(table.Name)}";
        static string QualifiedName(string schema, string table) => $"{SqlServerDdl.Q(schema)}.{SqlServerDdl.Q(table)}";
        static string SqlComment(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
        static string Columns(IEnumerable<string> columns) => string.Join(", ", columns.Select(SqlServerDdl.Q));
        static string Action(byte action) => action switch
        {
            1 => "CASCADE",
            2 => "SET NULL",
            3 => "SET DEFAULT",
            _ => "NO ACTION"
        };
        var selectedIds = string.Join(',', tables.Select(table => $"OBJECT_ID({Literal(Qualified(table.Design))}, N'U')"));
        var script = new StringBuilder();
        script.AppendLine("-- DB Studio TRUNCATE 数据清空计划。TRUNCATE 为最小日志操作，并非绝对零日志。");
        script.AppendLine("-- 执行前请备份并再次核对目标数据库与表清单；本脚本不会退回逐行 DELETE。");
        script.AppendLine($"-- 目标数据库：{SqlComment(database)}；表数量：{tables.Count}；生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        foreach (var table in tables)
        {
            script.AppendLine($"-- {SqlComment(Qualified(table.Design))} | {SqlComment(string.IsNullOrWhiteSpace(table.Design.Label) ? "未填写中文名" : table.Design.Label)} | {TableDataCategories.Get(table.Design.DataCategory).Name}");
        }
        script.AppendLine("SET NOCOUNT ON;");
        script.AppendLine("SET XACT_ABORT ON;");
        script.AppendLine($"IF DB_ID() <> DB_ID({Literal(database)}) THROW 51000, N'当前数据库不是清空计划指定的目标数据库。', 1;");
        foreach (var table in tables)
        {
            script.AppendLine($"IF OBJECT_ID({Literal(Qualified(table.Design))}, N'U') IS NULL THROW 51001, N'清空计划中的数据表不存在。', 1;");
        }
        script.AppendLine($"IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id IN ({selectedIds}) AND parent_object_id NOT IN ({selectedIds}))");
        script.AppendLine("    THROW 51002, N'所选表新增了范围外的外键引用，请重新生成清空计划。', 1;");
        script.AppendLine("BEGIN TRY");
        script.AppendLine("    BEGIN TRANSACTION;");
        var actualIds = tables.Select(table => table.ObjectId).ToHashSet();
        var internalKeys = foreignKeys.Where(key => actualIds.Contains(key.ParentObjectId)
            && actualIds.Contains(key.TargetObjectId) && key.ParentObjectId != key.TargetObjectId).ToList();
        foreach (var key in internalKeys)
        {
            var parent = QualifiedName(key.ParentSchema, key.ParentTable);
            var target = QualifiedName(key.TargetSchema, key.TargetTable);
            script.Append($"    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE parent_object_id=OBJECT_ID({Literal(parent)}, N'U') AND referenced_object_id=OBJECT_ID({Literal(target)}, N'U') AND name={Literal(key.Name)} AND delete_referential_action={key.DeleteAction} AND update_referential_action={key.UpdateAction} AND is_not_for_replication={(key.NotForReplication ? 1 : 0)} AND is_disabled={(key.Disabled ? 1 : 0)} AND is_not_trusted={(key.NotTrusted ? 1 : 0)}");
            script.Append($" AND (SELECT COUNT(*) FROM sys.foreign_key_columns WHERE constraint_object_id=fk.object_id)={key.ParentColumns.Count}");
            for (var columnIndex = 0; columnIndex < key.ParentColumns.Count; columnIndex++)
            {
                script.Append($" AND EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id WHERE fkc.constraint_object_id=fk.object_id AND fkc.constraint_column_id={columnIndex + 1} AND pc.name={Literal(key.ParentColumns[columnIndex])} AND rc.name={Literal(key.TargetColumns[columnIndex])})");
            }
            script.AppendLine(")");
            script.AppendLine("        THROW 51003, N'计划内外键结构或状态已变化，请重新生成清空计划。', 1;");
            script.AppendLine($"    ALTER TABLE {parent} DROP CONSTRAINT {SqlServerDdl.Q(key.Name)};");
        }
        foreach (var table in tables)
        {
            script.AppendLine($"    TRUNCATE TABLE {Qualified(table.Design)};");
        }
        foreach (var key in internalKeys.AsEnumerable().Reverse())
        {
            var parent = QualifiedName(key.ParentSchema, key.ParentTable);
            var target = QualifiedName(key.TargetSchema, key.TargetTable);
            var validation = !key.Disabled && !key.NotTrusted ? "CHECK" : "NOCHECK";
            script.Append($"    ALTER TABLE {parent} WITH {validation} ADD CONSTRAINT {SqlServerDdl.Q(key.Name)} FOREIGN KEY ({Columns(key.ParentColumns)}) REFERENCES {target} ({Columns(key.TargetColumns)})");
            script.Append($" ON DELETE {Action(key.DeleteAction)} ON UPDATE {Action(key.UpdateAction)}");
            if (key.NotForReplication)
            {
                script.Append(" NOT FOR REPLICATION");
            }
            script.AppendLine(";");
            script.AppendLine($"    ALTER TABLE {parent} {(key.Disabled ? "NOCHECK" : "CHECK")} CONSTRAINT {SqlServerDdl.Q(key.Name)};");
        }
        script.AppendLine("    COMMIT TRANSACTION;");
        script.AppendLine("END TRY");
        script.AppendLine("BEGIN CATCH");
        script.AppendLine("    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;");
        script.AppendLine("    THROW;");
        script.AppendLine("END CATCH;");
        return script.ToString();
    }
}
