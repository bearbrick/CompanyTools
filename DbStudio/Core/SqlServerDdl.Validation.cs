using System.Text.RegularExpressions;

namespace DbStudio.Core;

/// <summary>
/// 设计结构校验。不连接实际数据库，表达式语义和级联路径仍需在目标环境验证。
/// </summary>
public static partial class SqlServerDdl
{
    /// <summary>
    /// 检查当前表的变更及其他表对它的引用；预检查与保存共用同一规则。
    /// 调用前，项目快照中必须已包含当前表的待保存定义。
    /// </summary>
    /// <param name="project">包含待保存定义的项目快照。</param>
    /// <param name="table">本次检查的表定义。</param>
    /// <returns>当前表错误以及带来源表名前缀的入站引用错误。</returns>
    public static List<string> ValidateChange(DesignProject project, TableDesign table)
    {
        var errors = Validate(project, table);
        foreach (var incoming in project.Tables.Where(item =>
            item.Id != table.Id && item.ForeignKeys.Any(key => key.TargetTableId == table.Id)))
        {
            errors.AddRange(Validate(project, incoming).Select(error => incoming.Name + " / " + error));
        }
        return errors;
    }

    /// <summary>
    /// 汇总表、字段、索引、外键和 CHECK 的结构错误，一次返回全部问题。
    /// </summary>
    public static List<string> Validate(DesignProject project, TableDesign table)
    {
        var errors = new List<string>();
        void Need(bool valid, string message)
        {
            if (!valid)
            {
                errors.Add(message);
            }
        }
        bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
        bool Expression(string value) => !value.Contains(';') && !value.Contains("--") && !value.Contains("/*") && !Regex.IsMatch(value, @"\b(GO|CREATE|ALTER|DROP|EXEC|EXECUTE)\b", RegexOptions.IgnoreCase);
        Need(Identifier(table.Name) && Identifier(table.Schema), "表名和架构名必填，最多 128 字符，不能包含控制字符。");
        Need(
            !project.Tables.Any(t => t.Id != table.Id && string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Schema, table.Schema, StringComparison.OrdinalIgnoreCase)),
            "同一架构下的表名不能重复。");
        Need(table.Columns.Count > 0, "至少需要一个字段。");
        Need(table.Columns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == table.Columns.Count, "字段名不能重复（不区分大小写）。");
        Need(table.Columns.Count(c => c.Identity) <= 1, "一张表只能有一个自增字段。");
        Need(table.Columns.Count(c => c.Type == "rowversion") <= 1, "一张表只能有一个 rowversion 字段。");
        var pk = table.Columns.Where(c => c.PrimaryKeyOrder > 0).ToList();
        Need(table.PrimaryKeyName == "" || Identifier(table.PrimaryKeyName), "主键约束名称无效。");
        Need(Names(table.PrimaryKeyDescendingColumns).All(n => pk.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))), "主键降序字段必须属于主键。");
        Need(pk.Select(c => c.PrimaryKeyOrder).Distinct().Count() == pk.Count, "复合主键的顺序不能重复。");
        foreach (var c in table.Columns)
        {
            string p = $"{c.Name}：";
            Need(Identifier(c.Name), p + "字段名必填，最多 128 字符。");
            Need(Types.Contains(c.Type), p + "不支持的 SQL Server 类型。");
            Need(c.TemporalScale == null || c.TemporalScale is >= 0 and <= 7, p + "时间小数秒精度为 0～7。");
            Need(c.FloatPrecision == null || c.FloatPrecision is >= 1 and <= 53, p + "float 精度为 1～53。");
            if (HasLength(c.Type))
            {
                bool max = c.Length.Equals("max", StringComparison.OrdinalIgnoreCase) && c.Type is "varchar" or "nvarchar" or "varbinary";
                Need(
                    max || (int.TryParse(c.Length, out var length) && length > 0 && length <= (c.Type is "nvarchar" or "nchar" ? 4000 : 8000)),
                    p + "长度无效；变长类型可使用 max。");
            }
            if (c.Type is "decimal" or "numeric")
            {
                Need(c.Precision is >= 1 and <= 38 && c.Scale >= 0 && c.Scale <= c.Precision, p + "精度为 1–38，小数位不能超过精度。");
            }

            Need(c.PrimaryKeyOrder >= 0, p + "主键顺序不能为负数。");
            Need(
                c.PrimaryKeyOrder == 0 || (!c.Nullable && c.Computed == "" && c.Type is not ("xml" or "ntext" or "text" or "image") && !c.Length.Equals("max", StringComparison.OrdinalIgnoreCase)),
                p + "主键须非空，且此版本不支持计算列、LOB 或 max 主键。");
            if (c.Identity)
            {
                Need(
                    (c.Type is "bigint" or "int" or "smallint" or "tinyint" || (c.Type is "decimal" or "numeric" && c.Scale == 0)) && c.IdentityIncrement != 0 && !c.Nullable && c.Default == "" && c.Computed == "",
                    p + "自增须为非空整数类型（或小数位 0 的定点数），步长非零，不能同时设置默认值／计算表达式。");
            }

            Need(c.Computed == "" || (!c.Identity && c.Default == "" && c.PrimaryKeyOrder == 0), p + "计算列不能同时设置默认值、自增或主键。");
            Need(!c.Persisted || c.Computed != "", p + "PERSISTED 需要计算表达式。");
            Need(
                c.Collation == "" || (c.Type is "nvarchar" or "varchar" or "nchar" or "char" && Regex.IsMatch(c.Collation, "^[A-Za-z0-9_]+$")),
                p + "排序规则仅适用于字符类型，名称只允许字母数字和下划线。");
            Need(c.Type != "rowversion" || c.Default == "", p + "rowversion 不支持默认值。");
            Need(Expression(c.Default) && Expression(c.Computed), p + "请输入单个 SQL 表达式，不包含批处理或 DDL 语句。");
            Need(c.DefaultConstraintName == "" || Identifier(c.DefaultConstraintName), p + "默认约束名称最多 128 字符，不能包含控制字符。");
        }
        var constraintNames = pk.Count == 0 || table.PrimaryKeySystemNamed ? new List<string>() : new List<string> { table.PrimaryKeyName == "" ? "PK_" + table.Name : table.PrimaryKeyName };
        constraintNames.AddRange(table.Columns.Where(c => c.Default != "" && c.DefaultConstraintName != "" && !c.DefaultConstraintSystemNamed).Select(c => c.DefaultConstraintName));
        bool LocalColumns(string value, bool required = true)
        {
            var names = Names(value);
            return (!required || names.Length > 0) && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length && names.All(n => table.Columns.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var ix in table.Indexes)
        {
            Need(Identifier(ix.Name), "索引名称无效。");
            Need(LocalColumns(ix.Columns), $"{ix.Name}：索引列必须存在且不能重复。");
            Need(Names(ix.DescendingColumns).All(n => Names(ix.Columns).Contains(n, StringComparer.OrdinalIgnoreCase)), $"{ix.Name}：降序字段必须属于索引键列。");
            Need(LocalColumns(ix.Include, false), $"{ix.Name}：包含列无效。");
            Need(!Names(ix.Columns).Intersect(Names(ix.Include), StringComparer.OrdinalIgnoreCase).Any(), $"{ix.Name}：键列和包含列不能重复。");
            Need(!ix.IsConstraint || (ix.Unique && ix.Include == "" && ix.Filter == ""), $"{ix.Name}：唯一约束须勾选唯一，不能设置包含列／过滤条件。");
            Need(!ix.Clustered || ix.Include == "", $"{ix.Name}：聚集索引不能设置包含列。");
            Need(Expression(ix.Filter), $"{ix.Name}：过滤条件必须为单个表达式。");
            constraintNames.Add(ix.Name);
        }
        Need(table.Indexes.Count(i => i.Clustered) <= 1, "一张表只能有一个聚集索引。");
        Need(!(pk.Count > 0 && table.PrimaryKeyClustered == true && table.Indexes.Any(i => i.Clustered)), "聚集主键与其他聚集索引不能同时存在。");
        foreach (var fk in table.ForeignKeys)
        {
            constraintNames.Add(fk.Name);
            Need(Identifier(fk.Name), "外键名称无效。");
            var target = project.Tables.FirstOrDefault(t => t.Id == fk.TargetTableId);
            var local = Names(fk.Columns);
            var remote = Names(fk.TargetColumns);
            Need(LocalColumns(fk.Columns), $"{fk.Name}：本表字段无效。");
            Need(target != null && local.Length > 0 && local.Length == remote.Length, $"{fk.Name}：请选择引用表，两侧字段数量应一致。");
            Need(Actions.Contains(fk.OnDelete) && Actions.Contains(fk.OnUpdate), $"{fk.Name}：外键动作无效。");
            if (target != null)
            {
                bool existing = remote.All(n => target.Columns.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
                Need(existing, $"{fk.Name}：引用字段不存在。");
                var targetPk = target.Columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).Select(c => c.Name);
                Need(
                    remote.SequenceEqual(targetPk, StringComparer.OrdinalIgnoreCase) || target.Indexes.Any(ix => ix.Unique && ix.Filter == "" && remote.SequenceEqual(Names(ix.Columns), StringComparer.OrdinalIgnoreCase)),
                    $"{fk.Name}：引用字段必须匹配目标主键或未过滤的唯一键（含顺序）。");
                if (existing && LocalColumns(fk.Columns) && local.Length == remote.Length)
                {
                    for (int i = 0; i < local.Length; i++)
                    {
                        var a = table.Columns.First(c => c.Name.Equals(local[i], StringComparison.OrdinalIgnoreCase));
                        var b = target.Columns.First(c => c.Name.Equals(remote[i], StringComparison.OrdinalIgnoreCase));
                        Need(
                            a.Type == b.Type && (a.Type is not ("decimal" or "numeric") || a.Precision == b.Precision && a.Scale == b.Scale) && a.Collation == b.Collation,
                            $"{fk.Name}：{a.Name} 与引用字段类型／精度／排序规则不匹配。");
                        if (fk.OnDelete == "SET NULL" || fk.OnUpdate == "SET NULL")
                        {
                            Need(a.Nullable, $"{fk.Name}：SET NULL 要求本表字段允许空值。");
                        }

                        if (fk.OnDelete == "SET DEFAULT" || fk.OnUpdate == "SET DEFAULT")
                        {
                            Need(a.Nullable || a.Default != "", $"{fk.Name}：SET DEFAULT 要求字段有默认值或允许空值。");
                        }
                    }
                }
            }
        }
        foreach (var ck in table.Checks)
        {
            constraintNames.Add(ck.Name);
            Need(Identifier(ck.Name) && !string.IsNullOrWhiteSpace(ck.Expression) && Expression(ck.Expression), $"{ck.Name}：检查约束需要合法名称和单个表达式。");
        }
        Need(constraintNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == constraintNames.Count, "约束／索引名称不能重复。");
        return errors;
    }

}
