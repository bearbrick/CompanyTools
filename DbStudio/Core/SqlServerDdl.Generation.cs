using System.Text;

namespace DbStudio.Core;

/// <summary>
/// 从通过校验的设计生成可预览的 SQL Server 建表脚本，不执行结构变更。
/// </summary>
public static partial class SqlServerDdl
{
    /// <summary>
    /// 验证设计并生成建表脚本；不连接实际数据库，也不执行 SQL。
    /// </summary>
    public static string Generate(DesignProject project, TableDesign table, bool forModel = false)
    {
        var errors = Validate(project, table);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errors));
        }

        var name = Q(table.Schema) + "." + Q(table.Name);
        var sb = new StringBuilder($"-- DB Studio · SQL Server\n-- 设计预览；尚未在数据库执行。执行前请在目标环境验证表达式、依赖和级联路径。\n\n");
        if (!forModel)
        {
            sb.AppendLine($"IF SCHEMA_ID({Lit(table.Schema)}) IS NULL EXEC({Lit("CREATE SCHEMA " + Q(table.Schema))});\nGO\n");
        }

        var lines = new List<string>();
        foreach (var c in table.Columns)
        {
            if (c.Computed != "")
            {
                lines.Add($"    {Q(c.Name)} AS ({c.Computed}){(c.Persisted ? " PERSISTED" : "")}");
                continue;
            }
            var line = $"    {Q(c.Name)} {DataType(c)}";
            if (c.Collation != "")
            {
                line += " COLLATE " + c.Collation;
            }

            if (c.Identity)
            {
                line += $" IDENTITY({c.IdentitySeed},{c.IdentityIncrement})";
            }

            line += c.Nullable ? " NULL" : " NOT NULL";
            if (c.Default != "")
            {
                line += $" DEFAULT ({c.Default})";
            }

            lines.Add(line);
        }
        var pk = table.Columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).ToList();
        if (pk.Count > 0)
        {
            lines.Add($"    CONSTRAINT {Q(table.PrimaryKeyName == "" ? "PK_" + table.Name : table.PrimaryKeyName)} PRIMARY KEY {((table.PrimaryKeyClustered ?? !table.Indexes.Any(i => i.Clustered)) ? "CLUSTERED" : "NONCLUSTERED")} ({KeyList(string.Join(",", pk.Select(c => c.Name)), table.PrimaryKeyDescendingColumns)})");
        }

        foreach (var ix in table.Indexes.Where(i => i.IsConstraint))
        {
            lines.Add($"    CONSTRAINT {Q(ix.Name)} UNIQUE {(ix.Clustered ? "CLUSTERED" : "NONCLUSTERED")} ({KeyList(ix.Columns, ix.DescendingColumns)})");
        }

        foreach (var ck in table.Checks)
        {
            lines.Add($"    CONSTRAINT {Q(ck.Name)} CHECK ({ck.Expression})");
        }

        sb.AppendLine($"CREATE TABLE {name} (\n{string.Join(",\n", lines)}\n);\nGO\n");
        foreach (var ix in table.Indexes.Where(i => !i.IsConstraint))
        {
            sb.AppendLine($"CREATE {(ix.Unique ? "UNIQUE " : "")}{(ix.Clustered ? "CLUSTERED" : "NONCLUSTERED")} INDEX {Q(ix.Name)} ON {name} ({KeyList(ix.Columns, ix.DescendingColumns)}){(ix.Include == "" ? "" : " INCLUDE (" + List(ix.Include) + ")")}{(ix.Filter == "" ? "" : " WHERE " + ix.Filter)};\nGO\n");
        }

        foreach (var fk in table.ForeignKeys)
        {
            var target = project.Tables.First(t => t.Id == fk.TargetTableId);
            sb.AppendLine($"ALTER TABLE {name} ADD CONSTRAINT {Q(fk.Name)} FOREIGN KEY ({List(fk.Columns)}) REFERENCES {Q(target.Schema)}.{Q(target.Name)} ({List(fk.TargetColumns)}) ON DELETE {fk.OnDelete} ON UPDATE {fk.OnUpdate};\nGO\n");
        }
        void Description(string label, string? column = null)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            sb.AppendLine($"EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value={Lit(label)}, @level0type=N'SCHEMA', @level0name={Lit(table.Schema)}, @level1type=N'TABLE', @level1name={Lit(table.Name)}{(column == null ? "" : ", @level2type=N'COLUMN', @level2name=" + Lit(column))};");
        }
        Description(string.Join(" · ", new[] { table.Label, table.Comment }.Where(s => s != "")));
        foreach (var c in table.Columns)
        {
            Description(string.Join(" · ", new[] { c.Label, c.Comment }.Where(s => s != "")), c.Name);
        }

        return sb.ToString();
    }
}
