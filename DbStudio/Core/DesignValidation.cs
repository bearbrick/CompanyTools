namespace DbStudio.Core;

/// <summary>编辑和保存共用的设计完整性检查；物理 SQL 结构校验仍允许读取历史无说明字段。</summary>
public static class DesignValidation
{
    /// <summary>检查表结构、跨表依赖及字段业务定义的完整性。</summary>
    public static List<string> Check(DesignProject project, TableDesign table)
    {
        var issues = SqlServerDdl.ValidateChange(project, table);
        issues.AddRange(table.Columns.Select((column, index) => (column, index))
            .Where(item => string.IsNullOrWhiteSpace(item.column.Label))
            .Select(item => $"第 {item.index + 1} 行（{item.column.Name}）：定义名必填，请填写后再保存。"));
        return issues;
    }
}
