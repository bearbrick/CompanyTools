using System.Text;

namespace DbStudio.Core;

/// <summary>整项目建表脚本导出，与单表预览共享字段、索引和约束的生成规则。</summary>
public static partial class SqlServerDdl
{
    /// <summary>先声明 schema，再建立所有表，最后追加外键；不连接或修改实际数据库。</summary>
    public static string GenerateProject(DesignProject project)
    {
        if (project.Tables.Count == 0) { throw new InvalidOperationException("当前项目没有可导出的表。"); }
        var script = new StringBuilder("-- DB Studio · 项目 SQL 脚本\n-- 全部已保存表的建表脚本；请在预期的目标数据库执行。\n-- 此文件不是结构差异脚本，不包含数据库账号、权限或业务数据。\n\n");
        foreach (var schema in project.Tables.Select(t => t.Schema).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            script.AppendLine($"IF SCHEMA_ID({Lit(schema)}) IS NULL EXEC({Lit("CREATE SCHEMA " + Q(schema))});\nGO\n");
        }
        foreach (var table in project.Tables)
        {
            script.Append(GenerateCore(project, table, forModel: true, includeForeignKeys: false));
        }
        if (project.Tables.Any(t => t.ForeignKeys.Count > 0))
        {
            script.AppendLine("-- 所有表及唯一键建立完成后，再创建外键。\n");
            foreach (var table in project.Tables) { AppendForeignKeys(script, project, table); }
        }
        return script.ToString();
    }

    /// <summary>单表与项目导出共用外键生成，标识符和字符串均通过统一转义函数处理。</summary>
    private static void AppendForeignKeys(StringBuilder script, DesignProject project, TableDesign table)
    {
        var name = Q(table.Schema) + "." + Q(table.Name);
        foreach (var key in table.ForeignKeys)
        {
            var target = project.Tables.First(t => t.Id == key.TargetTableId);
            script.AppendLine($"ALTER TABLE {name} ADD CONSTRAINT {Q(key.Name)} FOREIGN KEY ({List(key.Columns)}) REFERENCES {Q(target.Schema)}.{Q(target.Name)} ({List(key.TargetColumns)}) ON DELETE {key.OnDelete} ON UPDATE {key.OnUpdate};\nGO\n");
        }
    }
}
