namespace DbStudio.Core;

/// <summary>统一模块显示顺序：已保存的模块在前，历史表中隐含的新模块依次追加。</summary>
public static class ModuleOrdering
{
    /// <summary>包含空模块且不改变项目快照；列表位置即模块序号，无须另存重复的数字字段。</summary>
    public static List<string> Names(DesignProject project) => project.Modules
        .Concat(project.Tables.Select(table => table.Module)).Distinct(StringComparer.Ordinal).ToList();
}
