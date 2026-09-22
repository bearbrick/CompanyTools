using System.Globalization;

namespace DbStudio.Core;

/// <summary>ER 图中的字段行；只展示理解关系所需的字段，或按用户选择展示全部字段。</summary>
public sealed record RelationshipDiagramColumn(string Name, string Label, string Type, bool PrimaryKey, bool RelationshipKey);

/// <summary>ER 图中的固定尺寸表节点；坐标由纯内存布局计算，浏览器只负责缩放和平移。</summary>
public sealed record RelationshipDiagramNode(string TableId, string Schema, string Name, string Label, string Module,
    double X, double Y, double Width, double Height, IReadOnlyList<RelationshipDiagramColumn> Columns, int HiddenColumns, bool ContextOnly);

/// <summary>一条可绘制的表关系；Path 为经过转义前的 SVG 路径数值，不包含任何业务输入。</summary>
public sealed record RelationshipDiagramEdge(string Name, string SourceTableId, string TargetTableId, string Columns,
    string TargetColumns, bool IsLogical, string Path, double LabelX, double LabelY);

/// <summary>一次筛选和布局后的 ER 图快照。</summary>
public sealed record RelationshipDiagramModel(IReadOnlyList<RelationshipDiagramNode> Nodes,
    IReadOnlyList<RelationshipDiagramEdge> Edges, double Width, double Height, int LogicalCount, int PhysicalCount);

/// <summary>
/// 项目 ER 图筛选与自动布局。关系方向为“本表字段 → 引用表字段”，布局时将引用表放在左侧，
/// 引用它的业务表放在右侧；循环关系留在同层并使用回绕连线。
/// </summary>
public static class RelationshipDiagramLayout
{
    private const double NodeWidth = 286;
    private const double HeaderHeight = 58;
    private const double RowHeight = 27;
    private const double HorizontalGap = 100;
    private const double VerticalGap = 34;
    private const double Margin = 42;
    private const int MaximumRows = 12;

    private sealed record Relation(TableDesign Source, TableDesign Target, ForeignKeyDesign Key);

    /// <summary>按模块、搜索和关系类型生成可直接渲染的节点、边与画布尺寸。</summary>
    public static RelationshipDiagramModel Build(DesignProject project, string module = "", string search = "",
        string relationType = "all", bool onlyRelated = true, bool showAllColumns = false)
    {
        if (relationType is not ("all" or "logical" or "physical"))
        {
            throw new ArgumentOutOfRangeException(nameof(relationType));
        }

        var tables = project.Tables.ToDictionary(table => table.Id, StringComparer.Ordinal);
        var relations = project.Tables.SelectMany(source => source.ForeignKeys.Select(key => new
        {
            Source = source,
            Target = tables.GetValueOrDefault(key.TargetTableId),
            Key = key
        }))
            .Where(item => item.Target != null
                && (relationType == "all" || relationType == "logical" && item.Key.IsLogical || relationType == "physical" && !item.Key.IsLogical))
            .Select(item => new Relation(item.Source, item.Target!, item.Key))
            .ToList();

        var normalizedSearch = search.Trim();
        bool Matches(TableDesign table) => normalizedSearch == ""
            || table.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
            || table.Label.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
            || table.Columns.Any(column => column.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                || column.Label.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase));

        var focusIds = project.Tables
            .Where(table => (module == "" || table.Module == module) && Matches(table))
            .Select(table => table.Id)
            .ToHashSet(StringComparer.Ordinal);
        var visibleIds = focusIds.ToHashSet(StringComparer.Ordinal);
        var focused = module != "" || normalizedSearch != "";
        if (focused)
        {
            foreach (var relation in relations.Where(relation => focusIds.Contains(relation.Source.Id) || focusIds.Contains(relation.Target.Id)))
            {
                visibleIds.Add(relation.Source.Id);
                visibleIds.Add(relation.Target.Id);
            }
        }
        else
        {
            visibleIds.UnionWith(project.Tables.Select(table => table.Id));
        }

        var relatedIds = relations.SelectMany(relation => new[] { relation.Source.Id, relation.Target.Id }).ToHashSet(StringComparer.Ordinal);
        if (onlyRelated)
        {
            visibleIds.IntersectWith(relatedIds);
        }

        var visibleTables = project.Tables.Where(table => visibleIds.Contains(table.Id)).ToList();
        var visibleRelations = relations.Where(relation => visibleIds.Contains(relation.Source.Id) && visibleIds.Contains(relation.Target.Id)).ToList();
        var levels = Levels(visibleTables, visibleRelations);
        var relationColumns = visibleTables.ToDictionary(table => table.Id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        foreach (var relation in visibleRelations)
        {
            relationColumns[relation.Source.Id].UnionWith(SqlServerDdl.Names(relation.Key.Columns));
            relationColumns[relation.Target.Id].UnionWith(SqlServerDdl.Names(relation.Key.TargetColumns));
        }

        var nodes = new List<RelationshipDiagramNode>();
        var levelX = Margin;
        foreach (var level in visibleTables.GroupBy(table => levels[table.Id]).OrderBy(group => group.Key))
        {
            var orderedTables = level.OrderBy(table => table.Module, StringComparer.OrdinalIgnoreCase)
                .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var lanes = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(orderedTables.Count)));
            var rowsPerLane = (int)Math.Ceiling((double)orderedTables.Count / lanes);
            var laneY = Enumerable.Repeat(Margin, lanes).ToArray();
            for (var tableIndex = 0; tableIndex < orderedTables.Count; tableIndex++)
            {
                var table = orderedTables[tableIndex];
                var lane = tableIndex / rowsPerLane;
                var important = relationColumns[table.Id];
                var selected = table.Columns.Where(column => showAllColumns || column.PrimaryKeyOrder > 0 || important.Contains(column.Name)).ToList();
                if (selected.Count == 0 && !onlyRelated)
                {
                    selected = table.Columns.Take(3).ToList();
                }
                var displayed = selected.Take(MaximumRows).Select(column => new RelationshipDiagramColumn(column.Name, column.Label,
                    column.Computed == "" ? SqlServerDdl.DataType(column) : "计算列", column.PrimaryKeyOrder > 0, important.Contains(column.Name))).ToList();
                var hidden = Math.Max(0, selected.Count - displayed.Count);
                var rows = Math.Max(1, displayed.Count);
                var nodeHeight = HeaderHeight + rows * RowHeight + (hidden > 0 ? 24 : 10);
                nodes.Add(new(table.Id, table.Schema, table.Name, table.Label, table.Module,
                    levelX + lane * (NodeWidth + HorizontalGap), laneY[lane], NodeWidth, nodeHeight, displayed, hidden,
                    focused && !focusIds.Contains(table.Id)));
                laneY[lane] += nodeHeight + VerticalGap;
            }
            levelX += lanes * (NodeWidth + HorizontalGap);
        }

        var nodeById = nodes.ToDictionary(node => node.TableId, StringComparer.Ordinal);
        var edges = new List<RelationshipDiagramEdge>();
        for (var index = 0; index < visibleRelations.Count; index++)
        {
            var relation = visibleRelations[index];
            var source = nodeById[relation.Source.Id];
            var target = nodeById[relation.Target.Id];
            var sourceY = ColumnAnchor(source, SqlServerDdl.Names(relation.Key.Columns).FirstOrDefault());
            var targetY = ColumnAnchor(target, SqlServerDdl.Names(relation.Key.TargetColumns).FirstOrDefault());
            var (path, labelX, labelY) = EdgePath(source, target, sourceY, targetY, index);
            edges.Add(new(relation.Key.Name, source.TableId, target.TableId, relation.Key.Columns,
                relation.Key.TargetColumns, relation.Key.IsLogical, path, labelX, labelY));
        }

        var width = nodes.Count == 0 ? 900 : Math.Max(900, nodes.Max(node => node.X + node.Width) + Margin);
        var height = nodes.Count == 0 ? 520 : Math.Max(520, nodes.Max(node => node.Y + node.Height) + Margin);
        return new(nodes, edges, width, height, edges.Count(edge => edge.IsLogical), edges.Count(edge => !edge.IsLogical));
    }

    /// <summary>用父表到子表的有向无环部分分层；循环中的节点保持同层，避免层级无限增长。</summary>
    private static Dictionary<string, int> Levels(IReadOnlyList<TableDesign> tables, IReadOnlyList<Relation> relations)
    {
        var levels = tables.ToDictionary(table => table.Id, _ => 0, StringComparer.Ordinal);
        var parents = tables.ToDictionary(table => table.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var children = tables.ToDictionary(table => table.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var relation in relations.Where(relation => relation.Source.Id != relation.Target.Id))
        {
            parents[relation.Source.Id].Add(relation.Target.Id);
            children[relation.Target.Id].Add(relation.Source.Id);
        }

        var remaining = parents.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
        var queue = new Queue<string>(tables.Where(table => remaining[table.Id] == 0)
            .OrderBy(table => table.Module, StringComparer.OrdinalIgnoreCase)
            .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Select(table => table.Id));
        var processed = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var parent))
        {
            if (!processed.Add(parent))
            {
                continue;
            }
            foreach (var child in children[parent])
            {
                levels[child] = Math.Max(levels[child], levels[parent] + 1);
                if (--remaining[child] == 0)
                {
                    queue.Enqueue(child);
                }
            }
        }

        var fallback = processed.Count == 0 ? 0 : levels.Where(pair => processed.Contains(pair.Key)).Max(pair => pair.Value) + 1;
        foreach (var table in tables.Where(table => !processed.Contains(table.Id)))
        {
            var knownParentLevels = parents[table.Id].Where(processed.Contains).Select(parent => levels[parent] + 1).ToList();
            levels[table.Id] = knownParentLevels.Count == 0 ? fallback : knownParentLevels.Max();
        }
        return levels;
    }

    private static double ColumnAnchor(RelationshipDiagramNode node, string? column)
    {
        var row = column == null ? -1 : node.Columns.ToList().FindIndex(item => item.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
        return node.Y + HeaderHeight + (row < 0 ? Math.Max(1, node.Columns.Count) * RowHeight / 2 : row * RowHeight + RowHeight / 2);
    }

    private static (string Path, double LabelX, double LabelY) EdgePath(RelationshipDiagramNode source,
        RelationshipDiagramNode target, double sourceY, double targetY, int index)
    {
        if (source.TableId == target.TableId)
        {
            var right = source.X + source.Width;
            var offset = 44 + index % 4 * 12;
            return ($"M {N(right)} {N(sourceY)} C {N(right + offset)} {N(sourceY)} {N(right + offset)} {N(sourceY + 58)} {N(right)} {N(sourceY + 58)}",
                right + offset, sourceY + 29);
        }

        if (source.X > target.X)
        {
            var startX = source.X;
            var endX = target.X + target.Width;
            var mid = (startX + endX) / 2;
            return ($"M {N(startX)} {N(sourceY)} C {N(mid)} {N(sourceY)} {N(mid)} {N(targetY)} {N(endX)} {N(targetY)}",
                mid, (sourceY + targetY) / 2);
        }

        var sourceRight = source.X + source.Width;
        var targetRight = target.X + target.Width;
        var bend = Math.Max(sourceRight, targetRight) + 50 + index % 5 * 10;
        return ($"M {N(sourceRight)} {N(sourceY)} C {N(bend)} {N(sourceY)} {N(bend)} {N(targetY)} {N(targetRight)} {N(targetY)}",
            bend, (sourceY + targetY) / 2);
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
