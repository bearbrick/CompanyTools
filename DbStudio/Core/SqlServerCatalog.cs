using System.Globalization;
using Microsoft.Data.SqlClient;

namespace DbStudio.Core;

/// <summary>只读 SQL Server 系统目录反推。读取结构，不读取业务表数据。</summary>
public static class SqlServerCatalog
{
    /// <summary>读取表、字段、主键、索引、外键和检查约束；无法无损表达的特性明确列为阻断提示。</summary>
    public static async Task<DatabaseSnapshot> ReadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = CatalogSql;
        using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var project = new DesignProject { Name = connection.Database };
        var warnings = new List<string>();
        var tables = new Dictionary<int, TableDesign>();
        while (await rows.ReadAsync(cancellationToken))
        {
            var table = new TableDesign { Id = rows.GetInt32(0).ToString(CultureInfo.InvariantCulture), Schema = rows.GetString(1), Name = rows.GetString(2), Label = rows.GetString(3), Module = rows.GetString(1) };
            tables.Add(rows.GetInt32(0), table);
            project.Tables.Add(table);
            if (rows.GetBoolean(4) || rows.GetByte(5) != 0)
            {
                warnings.Add($"{table.Schema}.{table.Name}：内存优化或时态表尚不能无损编辑。");
            }
        }
        await rows.NextResultAsync(cancellationToken);
        var columnNames = new Dictionary<(int, int), string>();
        while (await rows.ReadAsync(cancellationToken))
        {
            if (!tables.TryGetValue(rows.GetInt32(0), out var table))
            {
                continue;
            }
            var type = rows.GetString(3);
            if (type == "timestamp")
            {
                type = "rowversion";
            }
            var length = rows.GetInt16(4);
            var column = new ColumnDesign
            {
                Name = rows.GetString(2),
                Type = type,
                Length = length == -1 ? "max" : (type is "nvarchar" or "nchar" ? length / 2 : length).ToString(CultureInfo.InvariantCulture),
                Precision = rows.GetByte(5),
                Scale = rows.GetByte(6),
                Nullable = rows.GetBoolean(7),
                Identity = rows.GetBoolean(8),
                IdentitySeed = rows.IsDBNull(9) ? 1 : Convert.ToInt64(rows.GetValue(9)),
                IdentityIncrement = rows.IsDBNull(10) ? 1 : Convert.ToInt64(rows.GetValue(10)),
                Default = rows.GetString(11),
                Computed = rows.GetString(12),
                Persisted = rows.GetBoolean(13),
                Collation = rows.GetString(14),
                Label = rows.GetString(15)
            };
            // 空排序规则表示使用数据库默认值，避免把默认环境差异固化到设计中。
            if (column.Collation == rows.GetString(16))
            {
                column.Collation = "";
            }
            if (type is "datetime2" or "datetimeoffset" or "time")
            {
                column.TemporalScale = column.Scale;
            }
            if (type == "float")
            {
                column.FloatPrecision = column.Precision;
            }
            table.Columns.Add(column);
            columnNames[(rows.GetInt32(0), rows.GetInt32(1))] = column.Name;
            if (rows.GetBoolean(17) || rows.GetBoolean(18) || rows.GetInt32(19) != 0 || rows.GetBoolean(20) || rows.GetByte(21) != 0 || !SqlServerDdl.Types.Contains(type))
            {
                warnings.Add($"{table.Name}.{column.Name}：用户定义类型、稀疏列、类型化 XML、FILESTREAM 或生成列尚不能无损编辑。");
            }
        }
        await rows.NextResultAsync(cancellationToken);
        var indexes = new Dictionary<(int, int), IndexDesign>();
        while (await rows.ReadAsync(cancellationToken))
        {
            if (!tables.TryGetValue(rows.GetInt32(0), out var table))
            {
                continue;
            }
            var index = new IndexDesign { Name = rows.GetString(2), Unique = rows.GetBoolean(4), IsConstraint = rows.GetBoolean(6), Clustered = rows.GetByte(3) == 1, Filter = rows.GetString(7) };
            if (rows.GetBoolean(5))
            {
                table.PrimaryKeyName = index.Name;
                table.PrimaryKeyClustered = index.Clustered;
            }
            else
            {
                table.Indexes.Add(index);
            }
            indexes[(rows.GetInt32(0), rows.GetInt32(1))] = index;
            if (rows.GetByte(3) is not (1 or 2) || rows.GetBoolean(8) || rows.GetBoolean(9))
            {
                warnings.Add($"{table.Name}.{index.Name}：特殊索引、禁用索引或分区索引尚不能无损编辑。");
            }
        }
        await rows.NextResultAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            var key = (rows.GetInt32(0), rows.GetInt32(1));
            if (!indexes.TryGetValue(key, out var index) || !columnNames.TryGetValue((key.Item1, rows.GetInt32(2)), out var name))
            {
                continue;
            }
            var table = tables[key.Item1];
            if (rows.GetBoolean(4))
            {
                index.Include = Append(index.Include, name);
            }
            else
            {
                index.Columns = Append(index.Columns, name);
                if (table.PrimaryKeyName == index.Name)
                {
                    table.Columns.First(c => c.Name == name).PrimaryKeyOrder = rows.GetByte(3);
                }
                if (rows.GetBoolean(5))
                {
                    if (table.PrimaryKeyName == index.Name)
                    {
                        table.PrimaryKeyDescendingColumns = Append(table.PrimaryKeyDescendingColumns, name);
                    }
                    else
                    {
                        index.DescendingColumns = Append(index.DescendingColumns, name);
                    }
                }
            }
        }
        await rows.NextResultAsync(cancellationToken);
        var foreignKeys = new Dictionary<int, ForeignKeyDesign>();
        while (await rows.ReadAsync(cancellationToken))
        {
            if (!tables.TryGetValue(rows.GetInt32(1), out var table))
            {
                continue;
            }
            var foreignKey = new ForeignKeyDesign { Name = rows.GetString(3), TargetTableId = rows.GetInt32(2).ToString(CultureInfo.InvariantCulture), OnDelete = rows.GetString(4).Replace('_', ' '), OnUpdate = rows.GetString(5).Replace('_', ' ') };
            foreignKeys[rows.GetInt32(0)] = foreignKey;
            table.ForeignKeys.Add(foreignKey);
            if (rows.GetBoolean(6) || rows.GetBoolean(7) || rows.GetBoolean(8))
            {
                warnings.Add($"{table.Name}.{foreignKey.Name}：禁用、未信任或复制专用外键尚不能无损编辑。");
            }
        }
        await rows.NextResultAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            if (!foreignKeys.TryGetValue(rows.GetInt32(0), out var foreignKey))
            {
                continue;
            }
            foreignKey.Columns = Append(foreignKey.Columns, rows.GetString(1));
            foreignKey.TargetColumns = Append(foreignKey.TargetColumns, rows.GetString(2));
        }
        await rows.NextResultAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            if (!tables.TryGetValue(rows.GetInt32(0), out var table))
            {
                continue;
            }
            table.Checks.Add(new()
            {
                Name = rows.GetString(1),
                Expression = rows.GetString(2)
            });
            if (rows.GetBoolean(3) || rows.GetBoolean(4) || rows.GetBoolean(5))
            {
                warnings.Add($"{table.Name}.{rows.GetString(1)}：禁用、未信任或复制专用 CHECK 尚不能无损编辑。");
            }
        }
        warnings.AddRange(project.Tables.SelectMany(t => SqlServerDdl.Validate(project, t)).Distinct());
        return new(project, warnings.Distinct().ToList());
    }

    private static string Append(string value, string name) => value == "" ? name : value + "," + name;

    // 目录访问受 SQL Server 自身权限限制；要求 VIEW DEFINITION 避免把不可见对象误判为缺失。
    private const string CatalogSql = """
        IF HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') <> 1
            THROW 50001, N'读取结构需要当前数据库的 VIEW DEFINITION 权限。', 1;
        SELECT t.object_id,s.name,t.name,COALESCE(CONVERT(nvarchar(4000),ep.value),''),t.is_memory_optimized,t.temporal_type
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
        LEFT JOIN sys.extended_properties ep ON ep.class=1 AND ep.major_id=t.object_id AND ep.minor_id=0 AND ep.name='MS_Description'
        WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name;

        SELECT c.object_id,c.column_id,c.name,ty.name,c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,
            ic.seed_value,ic.increment_value,COALESCE(dc.definition,''),COALESCE(cc.definition,''),COALESCE(cc.is_persisted,CONVERT(bit,0)),
            COALESCE(c.collation_name,''),COALESCE(CONVERT(nvarchar(4000),ep.value),''),CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Collation')),
            ty.is_user_defined,c.is_sparse,c.xml_collection_id,c.is_filestream,c.generated_always_type
        FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
        LEFT JOIN sys.extended_properties ep ON ep.class=1 AND ep.major_id=c.object_id AND ep.minor_id=c.column_id AND ep.name='MS_Description'
        WHERE t.is_ms_shipped=0 ORDER BY c.object_id,c.column_id;

        SELECT i.object_id,i.index_id,i.name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,COALESCE(i.filter_definition,''),i.is_disabled,CONVERT(bit,CASE WHEN ds.type='PS' THEN 1 ELSE 0 END)
        FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id LEFT JOIN sys.data_spaces ds ON ds.data_space_id=i.data_space_id
        WHERE t.is_ms_shipped=0 AND i.index_id>0 AND i.is_hypothetical=0 ORDER BY i.object_id,i.index_id;
        SELECT ic.object_id,ic.index_id,ic.column_id,ic.key_ordinal,ic.is_included_column,ic.is_descending_key
        FROM sys.index_columns ic JOIN sys.tables t ON t.object_id=ic.object_id WHERE t.is_ms_shipped=0 ORDER BY ic.object_id,ic.index_id,ic.is_included_column,ic.key_ordinal,ic.index_column_id;
        SELECT object_id,parent_object_id,referenced_object_id,name,delete_referential_action_desc,update_referential_action_desc,is_disabled,is_not_trusted,is_not_for_replication FROM sys.foreign_keys ORDER BY object_id;
        SELECT f.constraint_object_id,l.name,r.name FROM sys.foreign_key_columns f
        JOIN sys.columns l ON l.object_id=f.parent_object_id AND l.column_id=f.parent_column_id
        JOIN sys.columns r ON r.object_id=f.referenced_object_id AND r.column_id=f.referenced_column_id ORDER BY f.constraint_object_id,f.constraint_column_id;
        SELECT parent_object_id,name,definition,is_disabled,is_not_trusted,is_not_for_replication FROM sys.check_constraints ORDER BY parent_object_id,name;
        """;
}
