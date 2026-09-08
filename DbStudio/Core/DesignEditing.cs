namespace DbStudio.Core;

/// <summary>
/// 设计草稿的纯内存操作。返回独立对象，不修改源项目，也不直接写入数据库。
/// </summary>
public static class DesignEditing
{
    /// <summary>
    /// 复制完整表结构，生成不重复的表名、表和字段 ID，以及新的约束名称。
    /// 自引用外键改为指向副本，对其他表的引用保持原目标。
    /// </summary>
    /// <param name="project">用于检测同一架构下名称冲突的项目快照。</param>
    /// <param name="source">源表；允许包含尚未保存的编辑。</param>
    /// <returns>可继续编辑并通过正常保存流程提交的新表草稿。</returns>
    public static TableDesign CopyTable(DesignProject project, TableDesign source)
    {
        // 为约束前缀与序号预留空间，避免合法的长表名复制后产生超长标识符。
        var baseName = source.Name.Length > 100 ? source.Name[..100] : source.Name;
        var candidate = baseName + "_copy";
        var sequence = 2;
        while (project.Tables.Any(table =>
            table.Schema.Equals(source.Schema, StringComparison.OrdinalIgnoreCase)
            && table.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseName + "_copy" + sequence++;
        }

        return CopyTable(project, source, new TableCopyOptions
        {
            Name = candidate,
            Schema = source.Schema,
            Label = source.Label + "（副本）",
            Module = source.Module,
            Comment = source.Comment
        });
    }

    /// <summary>
    /// 按用户填写的新表信息复制完整设计。字段业务配置不变，独立标识及约束名称重新生成。
    /// 此方法仅构造草稿；权限、结构有效性及并发版本由正常保存流程统一检查。
    /// </summary>
    public static TableDesign CopyTable(DesignProject project, TableDesign source, TableCopyOptions options)
    {
        var copy = ModelJson.Clone(source);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = options.Name.Trim();
        copy.Schema = options.Schema.Trim();
        copy.Label = options.Label.Trim();
        copy.Module = string.IsNullOrWhiteSpace(options.Module) ? "未分组" : options.Module.Trim();
        copy.Comment = options.Comment;
        if (string.IsNullOrWhiteSpace(copy.Name) || string.IsNullOrWhiteSpace(copy.Schema)
            || copy.Name.Length > 128 || copy.Schema.Length > 128
            || copy.Name.Any(char.IsControl) || copy.Schema.Any(char.IsControl))
        {
            throw new InvalidOperationException("表名和架构名必填，最多 128 字符，不能包含控制字符。");
        }
        if (project.Tables.Any(table => table.Schema.Equals(copy.Schema, StringComparison.OrdinalIgnoreCase)
            && table.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("同一架构下已存在该表名，请为新表填写其他名称。");
        }

        // 约束名在架构内共享命名空间，不能仅把原表名替换成新表名。
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in project.Tables.Where(table => table.Schema.Equals(copy.Schema, StringComparison.OrdinalIgnoreCase)))
        {
            reserved.Add(table.Name);
            reserved.Add(table.PrimaryKeyName == "" ? "PK_" + table.Name : table.PrimaryKeyName);
            reserved.UnionWith(table.Columns.Select(column => column.DefaultConstraintName));
            reserved.UnionWith(table.Indexes.Select(index => index.Name));
            reserved.UnionWith(table.ForeignKeys.Select(key => key.Name));
            reserved.UnionWith(table.Checks.Select(check => check.Name));
        }
        reserved.Add(copy.Name);
        copy.PrimaryKeyName = copy.Columns.Any(column => column.PrimaryKeyOrder > 0)
            ? AllocateName("PK_" + copy.Name, reserved) : "";
        copy.PrimaryKeySystemNamed = false;
        foreach (var column in copy.Columns)
        {
            column.Id = Guid.NewGuid().ToString("N");
            column.DefaultConstraintName = "";
            column.DefaultConstraintSystemNamed = false;
        }

        // 不替换 SQL 表达式中的文字，避免改变条件或字面量的实际含义。
        for (var index = 0; index < copy.Indexes.Count; index++)
        {
            var item = copy.Indexes[index];
            item.Name = AllocateName($"{(item.IsConstraint ? "UQ" : "IX")}_{copy.Name}_{index + 1}", reserved);
        }
        for (var index = 0; index < copy.ForeignKeys.Count; index++)
        {
            var item = copy.ForeignKeys[index];
            item.Name = AllocateName($"FK_{copy.Name}_{index + 1}", reserved);
            if (item.TargetTableId == source.Id)
            {
                item.TargetTableId = copy.Id;
            }
        }
        for (var index = 0; index < copy.Checks.Count; index++)
        {
            copy.Checks[index].Name = AllocateName($"CK_{copy.Name}_{index + 1}", reserved);
        }

        return copy;
    }

    /// <summary>为 SQL Server 标识符保留序号空间，兼容长表名和已有自定义约束名。</summary>
    private static string AllocateName(string basis, HashSet<string> reserved)
    {
        var stem = basis.Length > 118 ? basis[..118] : basis;
        var candidate = stem;
        var sequence = 2;
        while (!reserved.Add(candidate))
        {
            candidate = stem + "_" + sequence++;
        }
        return candidate;
    }
}
