using DbStudio.Core;
using Microsoft.AspNetCore.Components;

namespace DbStudio.Components;

/// <summary>只根据当前成员已获授权的项目快照展示概览，不连接实际数据库。</summary>
public partial class ProjectOverview
{
    /// <summary>当前已授权的已保存项目快照。</summary>
    [Parameter, EditorRequired] public DesignProject Project { get; set; } = default!;
    /// <summary>当前成员是否可进入设计操作。</summary>
    [Parameter]
    public bool CanDesign
    {
        get; set;
    }
    /// <summary>用户请求创建新表时的回调。</summary>
    [Parameter]
    public EventCallback NewTableRequested
    {
        get; set;
    }
    /// <summary>用户请求刷新项目统计时的回调。</summary>
    [Parameter]
    public EventCallback RefreshRequested
    {
        get; set;
    }
    /// <summary>用户请求打开 ER 图时的回调。</summary>
    [Parameter]
    public EventCallback DiagramRequested
    {
        get; set;
    }
    /// <summary>用户请求打开版本历史时的回调。</summary>
    [Parameter]
    public EventCallback VersionRequested
    {
        get; set;
    }
    /// <summary>当前项目最近的审计活动。</summary>
    [Parameter] public IReadOnlyList<AuditItem> Activities { get; set; } = [];

    private List<(string Label, int Value, string Unit)> Metrics = [];
    private List<(string Name, int Tables, int Columns)> Modules = [];
    /// <summary>沿用操作记录页的本地时间显示；历史异常时间保留原文，不中断首页渲染。</summary>
    private static string ActivityTime(string value) => DateTimeOffset.TryParse(value, out var time)
        ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : value;

    /// <summary>采用保存快照重新统计。索引数量仅统计索引面板中的定义，不重复计算字段主键。</summary>
    protected override void OnParametersSet()
    {
        Modules = ModuleOrdering.Names(Project).Select(name =>
        {
            var tables = Project.Tables.Where(table => table.Module == name).ToList();
            return (name, tables.Count, tables.Sum(table => table.Columns.Count));
        }).ToList();
        Metrics = [("数据表", Project.Tables.Count, "张表"), ("字段", Project.Tables.Sum(t => t.Columns.Count), "个字段"),
            ("业务模块", Modules.Count, "个模块"), ("索引与唯一约束", Project.Tables.Sum(t => t.Indexes.Count), "项定义 · 不含主键"),
            ("表关系", Project.Tables.Sum(t => t.ForeignKeys.Count), $"逻辑 {Project.Tables.Sum(t => t.ForeignKeys.Count(key => key.IsLogical))} · 物理 {Project.Tables.Sum(t => t.ForeignKeys.Count(key => !key.IsLogical))}"), ("检查约束", Project.Tables.Sum(t => t.Checks.Count), "项约束")];
    }
}
