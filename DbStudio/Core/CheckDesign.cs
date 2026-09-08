namespace DbStudio.Core;

/// <summary>
/// 限制字段取值的表级 CHECK 约束。
/// </summary>
public class CheckDesign
{
    /// <summary>
    /// 检查约束名称。
    /// </summary>
    public string Name { get; set; } = "CK_New";

    /// <summary>
    /// 单个 SQL 条件表达式，不含 CHECK 关键字。
    /// </summary>
    public string Expression { get; set; } = "";
}
