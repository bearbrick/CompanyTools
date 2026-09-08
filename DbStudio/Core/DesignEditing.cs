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
        var copy = ModelJson.Clone(source);
        copy.Id = Guid.NewGuid().ToString("N");

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

        copy.Name = candidate;
        copy.PrimaryKeyName = "";
        copy.Label = source.Label + "（副本）";
        foreach (var column in copy.Columns)
        {
            column.Id = Guid.NewGuid().ToString("N");
        }

        // 不替换 SQL 表达式中的文字，避免改变条件或字面量的实际含义。
        for (var index = 0; index < copy.Indexes.Count; index++)
        {
            var item = copy.Indexes[index];
            item.Name = $"{(item.IsConstraint ? "UQ" : "IX")}_{copy.Name}_{index + 1}";
        }
        for (var index = 0; index < copy.ForeignKeys.Count; index++)
        {
            var item = copy.ForeignKeys[index];
            item.Name = $"FK_{copy.Name}_{index + 1}";
            if (item.TargetTableId == source.Id)
            {
                item.TargetTableId = copy.Id;
            }
        }
        for (var index = 0; index < copy.Checks.Count; index++)
        {
            copy.Checks[index].Name = $"CK_{copy.Name}_{index + 1}";
        }

        return copy;
    }
}
