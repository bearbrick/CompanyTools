namespace DbStudio.Core;

/// <summary>
/// 项目内的表关系，两侧字段按顺序一一对应；可仅用于设计，也可生成数据库外键。
/// </summary>
public class ForeignKeyDesign
{
    /// <summary>
    /// 是否为仅保存在设计中的逻辑关系。旧项目未包含此属性时保持 false，继续视为物理外键。
    /// </summary>
    public bool IsLogical { get; set; }

    /// <summary>
    /// 关系名称；物理外键同时将其用作数据库约束名称。
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
    /// 删除目标记录时的参照动作；仅物理外键使用。
    /// </summary>
    public string OnDelete { get; set; } = "NO ACTION";

    /// <summary>
    /// 更新目标键值时的参照动作；仅物理外键使用。
    /// </summary>
    public string OnUpdate { get; set; } = "NO ACTION";
}
