namespace DbStudio.Core;

/// <summary>
/// 项目内的外键关系，两侧字段按顺序一一对应。
/// </summary>
public class ForeignKeyDesign
{
    /// <summary>
    /// 外键约束名称。
    /// </summary>
    public string Name { get; set; } = "FK_New";

    /// <summary>
    /// 本表字段，英文逗号分隔。
    /// </summary>
    public string Columns { get; set; } = "";

    /// <summary>
    /// 被引用表的设计 ID，与表名变更解耦。
    /// </summary>
    public string TargetTableId { get; set; } = "";

    /// <summary>
    /// 目标主键或唯一键字段，英文逗号分隔。
    /// </summary>
    public string TargetColumns { get; set; } = "";

    /// <summary>
    /// 删除目标记录时的参照动作。
    /// </summary>
    public string OnDelete { get; set; } = "NO ACTION";

    /// <summary>
    /// 更新目标键值时的参照动作。
    /// </summary>
    public string OnUpdate { get; set; } = "NO ACTION";
}
