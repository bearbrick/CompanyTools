namespace DbStudio.Core;

/// <summary>PDF 归档的文档信息；只作用于本次导出，不修改项目或伪造审批结果。</summary>
public sealed class StructureArchiveOptions
{
    /// <summary>档案编号，最多 80 字符。</summary>
    public string DocumentNumber { get; set; } = "";
    /// <summary>归档文档版本，与项目修订号分别记录。</summary>
    public string Version { get; set; } = "V1.0";
    /// <summary>待归档数据库名，由用户填写，不推测实际连接。</summary>
    public string DatabaseName { get; set; } = "";
    /// <summary>数据库产品版本；未提供时不声称已核验实际环境。</summary>
    public string DatabaseVersion { get; set; } = "";
    /// <summary>生产、测试等来源环境的文字说明。</summary>
    public string Environment { get; set; } = "";
    /// <summary>编制部门。</summary>
    public string Department { get; set; } = "";
    /// <summary>编制人；签字栏仍留待本人确认。</summary>
    public string PreparedBy { get; set; } = "";
    /// <summary>文档密级或分发范围。</summary>
    public string Classification { get; set; } = "内部";

    /// <summary>约束首页输入长度，避免超长或控制字符破坏正式版式；服务端必须调用。</summary>
    public void Validate()
    {
        foreach (var (name, value, max) in new[]
        {
            ("文档编号", DocumentNumber, 80), ("文档版本", Version, 30),
            ("数据库名称", DatabaseName, 128), ("数据库版本", DatabaseVersion, 80),
            ("来源环境", Environment, 40), ("编制部门", Department, 80),
            ("编制人", PreparedBy, 50), ("密级", Classification, 30)
        })
        {
            if (value == null || value.Length > max || value.Any(char.IsControl))
            {
                throw new InvalidOperationException($"{name}不能超过 {max} 字符，且不能包含换行或控制字符。");
            }
        }
        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidOperationException("请填写文档版本。");
        }
    }
}

/// <summary>一次经过权限与修订号检查的已保存设计，不含连接配置或业务数据。</summary>
public sealed record StructureArchive(DesignProject Project, StructureArchiveOptions Options, DateTimeOffset ExportedAt)
{
    /// <summary>下载文件名去除路径、控制字符，并限制长度以兼容 Windows。</summary>
    public string FileName
    {
        get
        {
            var name = string.Concat(Project.Name.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c))).Trim().TrimEnd('.');
            name = string.Concat(name.EnumerateRunes().Take(60).Select(r => r.ToString()));
            return $"{(name.Length == 0 ? "项目" : name)}_数据库结构_r{Project.Revision}_{ExportedAt:yyyyMMdd}.pdf";
        }
    }
}

/// <summary>将真实 SQL Server 模型转成归档正文，保留字段和约束语义，不使用 NN 等界面缩写。</summary>
public static class StructureArchiveText
{
    /// <summary>按模块显示顺序及项目内表顺序排列，不受页面搜索或折叠状态影响。</summary>
    public static IEnumerable<TableDesign> Tables(DesignProject project) => ModuleOrdering.Names(project)
        .SelectMany(module => project.Tables.Where(table => table.Module == module));

    /// <summary>空值限制留白表示可空；计算列的空值性由表达式决定，不能照搬普通字段开关。</summary>
    public static string Nullability(ColumnDesign column) => column.Computed != "" ? "表达式推导" : column.Nullable ? "" : "不可空";

    /// <summary>计算列不使用设计器的占位基础类型，避免将推导类型错误归档为已核验类型。</summary>
    public static string DataType(ColumnDesign column) => column.Computed == "" ? SqlServerDdl.DataType(column) : "计算列";

    /// <summary>默认值与生成方式分清 NULL、无默认、自增和持久化计算表达式。</summary>
    public static string Generation(ColumnDesign column)
    {
        if (column.Computed != "")
        {
            return $"{(column.Persisted ? "持久化计算" : "计算")}：{column.Computed}";
        }
        if (column.Identity)
        {
            return $"自增：起始 {column.IdentitySeed}，步长 {column.IdentityIncrement}";
        }
        return string.IsNullOrWhiteSpace(column.Default) ? "无" : column.Default;
    }

    /// <summary>业务名称和设计备注均保留，不把备注混为数据库扩展属性。</summary>
    public static string Description(ColumnDesign column) => string.Join("\n", new[]
    {
        column.Label,
        column.Comment == "" ? "" : "备注：" + column.Comment,
        column.InputLimit == "" ? "" : "输入位数：" + column.InputLimit
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>表下集中列明主键、索引、外键、检查约束及特殊字段属性，不丢弃列顺序或引用动作。</summary>
    public static IEnumerable<string> Notes(DesignProject project, TableDesign table)
    {
        var keys = table.Columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).ToList();
        if (keys.Count > 0)
        {
            var name = table.PrimaryKeyName == "" ? "PK_" + table.Name : table.PrimaryKeyName;
            yield return $"主键：{name}；{KeyColumns(string.Join(',', keys.Select(c => c.Name)), table.PrimaryKeyDescendingColumns)}；{((table.PrimaryKeyClustered ?? !table.Indexes.Any(i => i.Clustered)) ? "聚集" : "非聚集")}{(table.PrimaryKeySystemNamed ? "；系统命名" : "")}。";
        }
        foreach (var index in table.Indexes)
        {
            yield return $"{(index.IsConstraint ? "唯一约束" : index.Unique ? "唯一索引" : "普通索引")}：{index.Name}；{KeyColumns(index.Columns, index.DescendingColumns)}；{(index.Clustered ? "聚集" : "非聚集")}{(index.Include == "" ? "" : "；包含列：" + index.Include)}{(index.Filter == "" ? "" : "；筛选条件：" + index.Filter)}。";
        }
        foreach (var key in table.ForeignKeys)
        {
            var target = project.Tables.FirstOrDefault(t => t.Id == key.TargetTableId);
            var targetName = target == null ? "[目标表缺失，待核验]" : $"{target.Schema}.{target.Name}";
            yield return $"外键：{key.Name}；({key.Columns}) → {targetName} ({key.TargetColumns})；删除：{ActionName(key.OnDelete)}；更新：{ActionName(key.OnUpdate)}。";
        }
        foreach (var check in table.Checks)
        {
            yield return $"检查约束：{check.Name}；{check.Expression}";
        }
        foreach (var column in table.Columns)
        {
            if (column.Collation != "")
            {
                yield return $"字段排序规则：{column.Name} = {column.Collation}";
            }
            if (column.Default != "" && column.DefaultConstraintName != "")
            {
                yield return $"默认约束：{column.Name} = {column.DefaultConstraintName}{(column.DefaultConstraintSystemNamed ? "（系统命名）" : "")}";
            }
        }
    }

    private static string KeyColumns(string columns, string descending)
    {
        var desc = SqlServerDdl.Names(descending).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join("，", SqlServerDdl.Names(columns).Select(name => name + (desc.Contains(name) ? " 降序" : " 升序")));
    }

    private static string ActionName(string action) => action switch
    {
        "NO ACTION" => "限制（NO ACTION）",
        "CASCADE" => "级联（CASCADE）",
        "SET NULL" => "设为空值（SET NULL）",
        "SET DEFAULT" => "设为默认值（SET DEFAULT）",
        _ => action
    };
}
