namespace DbStudio.Components;

/// <summary>
/// 统一选择器的选项。值供业务保存，文字、说明和分类只用于呈现与搜索。
/// </summary>
/// <param name="Value">稳定的标识或数据库语法值。</param>
/// <param name="Text">面向用户的名称。</param>
/// <param name="Description">辅助说明，例如物理表名或类型用途。</param>
/// <param name="Group">用于快速辨识和搜索的分类。</param>
public record SelectOption(string Value, string Text, string Description = "", string Group = "");
