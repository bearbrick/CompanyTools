namespace DbStudio.Core;

/// <summary>
/// 独立项目的设计快照，使用 Revision 进行乐观并发控制。
/// </summary>
public class DesignProject
{
    /// <summary>
    /// 项目稳定标识。
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 项目显示名称。
    /// </summary>
    public string Name { get; set; } = "新项目";

    /// <summary>
    /// 项目用途说明。
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// 数据库方言；当前支持 SqlServer。
    /// </summary>
    public string Dialect { get; set; } = "SqlServer";

    /// <summary>
    /// 保存版本号；提交旧版本将被拒绝。
    /// </summary>
    public int Revision
    {
        get; set;
    }

    /// <summary>
    /// 项目中的全部表定义。
    /// </summary>
    public List<TableDesign> Tables { get; set; } = [];

    /// <summary>独立模块名称，允许尚未包含数据表的空模块。</summary>
    public List<string> Modules { get; set; } = [];
}
