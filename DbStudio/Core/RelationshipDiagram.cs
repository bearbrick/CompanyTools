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

    /// <summary>按项目配置识别技术字段建立的关系，复合关系只要包含技术字段便视为技术关系。</summary>
    public static bool IsTechnicalRelation(DesignProject project, ForeignKeyDesign key)
        => TechnicalRelation(key, (project.TechnicalFields ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static bool TechnicalRelation(ForeignKeyDesign key, HashSet<string> technicalNames)
        => SqlServerDdl.Names(key.Columns).Any(technicalNames.Contains)
            || SqlServerDdl.Names(key.TargetColumns).Any(technicalNames.Contains);

    /// <summary>按模块、搜索和关系类型生成可直接渲染的节点、边与画布尺寸。</summary>
    public static RelationshipDiagramModel Build(DesignProject project, string module = "", string search = "",
        string relationType = "all", bool onlyRelated = true, bool showAllColumns = false,
        string focusTableId = "", bool showAllRelations = true, bool showTechnicalFields = true,
        string focusDirection = "both")
    {
        if (relationType is not ("all" or "logical" or "physical"))
        {
            throw new ArgumentOutOfRangeException(nameof(relationType));
        }
        if (focusDirection is not ("outgoing" or "incoming" or "both"))
        {
            throw new ArgumentOutOfRangeException(nameof(focusDirection));
        }

        var technicalNames = (project.TechnicalFields ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tables = project.Tables.ToDictionary(table => table.Id, StringComparer.Ordinal);
        var relations = project.Tables.SelectMany(source => source.ForeignKeys.Select(key => new
        {
            Source = source,
            Target = tables.GetValueOrDefault(key.TargetTableId),
            Key = key
        }))
            .Where(item => item.Target != null
                && (showTechnicalFields || !TechnicalRelation(item.Key, technicalNames))
                && (relationType == "all" || relationType == "logical" && item.Key.IsLogical || relationType == "physical" && !item.Key.IsLogical))
            .Select(item => new Relation(item.Source, item.Target!, item.Key))
            .ToList();
        var focusedRelations = focusTableId == "" ? relations : relations
            .Where(relation => focusDirection switch
            {
                "outgoing" => relation.Source.Id == focusTableId,
                "incoming" => relation.Target.Id == focusTableId,
                _ => relation.Source.Id == focusTableId || relation.Target.Id == focusTableId
            }).ToList();

        var normalizedSearch = search.Trim();
        bool Matches(TableDesign table) => normalizedSearch == ""
            || table.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
            || table.Label.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
            || table.Columns.Any(column => column.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                || column.Label.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase));

        var focusIds = focusTableId == ""
            ? project.Tables.Where(table => (module == "" || table.Module == module) && Matches(table))
                .Select(table => table.Id).ToHashSet(StringComparer.Ordinal)
            : project.Tables.Where(table => table.Id == focusTableId).Select(table => table.Id).ToHashSet(StringComparer.Ordinal);
        var visibleIds = focusIds.ToHashSet(StringComparer.Ordinal);
        var focused = module != "" || normalizedSearch != "" || focusTableId != "";
        if (focusTableId != "" || focused && showAllRelations)
        {
            foreach (var relation in focusedRelations.Where(relation => focusIds.Contains(relation.Source.Id) || focusIds.Contains(relation.Target.Id)))
            {
                visibleIds.Add(relation.Source.Id);
                visibleIds.Add(relation.Target.Id);
            }
        }
        else if (!focused)
        {
            visibleIds.UnionWith(project.Tables.Select(table => table.Id));
        }

        var relatedIds = relations.SelectMany(relation => new[] { relation.Source.Id, relation.Target.Id }).ToHashSet(StringComparer.Ordinal);
        if (onlyRelated && focusTableId == "")
        {
            visibleIds.IntersectWith(relatedIds);
        }

        var visibleTables = project.Tables.Where(table => visibleIds.Contains(table.Id)).ToList();
        var visibleRelations = focusedRelations.Where(relation => showAllRelations || focusTableId != "")
            .Where(relation => visibleIds.Contains(relation.Source.Id) && visibleIds.Contains(relation.Target.Id))
            .Where(relation => !focused || focusIds.Contains(relation.Source.Id) || focusIds.Contains(relation.Target.Id))
            .ToList();
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
                var selected = table.Columns.Where(column => (showTechnicalFields || !technicalNames.Contains(column.Name))
                    && (showAllColumns || column.PrimaryKeyOrder > 0 || important.Contains(column.Name))).ToList();
                if (selected.Count == 0 && !onlyRelated)
                {
                    selected = table.Columns.Where(column => showTechnicalFields || !technicalNames.Contains(column.Name)).Take(3).ToList();
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

        if (focusTableId != "" && nodes.Any(node => node.TableId == focusTableId))
        {
            nodes = ArrangeFocused(nodes, visibleRelations, project.Tables.First(table => table.Id == focusTableId));
        }

        var nodeById = nodes.ToDictionary(node => node.TableId, StringComparer.Ordinal);
        var edges = new List<RelationshipDiagramEdge>();
        var sourceRoutes = new Dictionary<string, int>(StringComparer.Ordinal);
        var selfRoutes = new Dictionary<string, int>(StringComparer.Ordinal);
        var targetRoutes = new Dictionary<(string TableId, string Columns), int>();
        var targetTotals = visibleRelations.GroupBy(relation => (relation.Target.Id, relation.Key.TargetColumns), relation => relation)
            .ToDictionary(group => group.Key, group => group.Count());
        for (var index = 0; index < visibleRelations.Count; index++)
        {
            var relation = visibleRelations[index];
            var source = nodeById[relation.Source.Id];
            var target = nodeById[relation.Target.Id];
            var sourceY = ColumnAnchor(source, SqlServerDdl.Names(relation.Key.Columns).FirstOrDefault());
            var targetKey = (relation.Target.Id, relation.Key.TargetColumns);
            var targetIndex = targetRoutes.GetValueOrDefault(targetKey);
            targetRoutes[targetKey] = targetIndex + 1;
            var targetY = ColumnAnchor(target, SqlServerDdl.Names(relation.Key.TargetColumns).FirstOrDefault())
                + (targetTotals[targetKey] == 1 ? 0 : (targetIndex - (targetTotals[targetKey] - 1) / 2.0) * Math.Min(7, 18.0 / (targetTotals[targetKey] - 1)));
            var sourceRoute = sourceRoutes.GetValueOrDefault(source.TableId);
            sourceRoutes[source.TableId] = sourceRoute + 1;
            var selfRoute = selfRoutes.GetValueOrDefault(source.TableId);
            if (source.TableId == target.TableId)
            {
                selfRoutes[source.TableId] = selfRoute + 1;
            }
            var (path, labelX, labelY) = EdgePath(source, target, sourceY, targetY, sourceRoute, selfRoute,
                focusTableId != "");
            edges.Add(new(relation.Key.Name, source.TableId, target.TableId, relation.Key.Columns,
                relation.Key.TargetColumns, relation.Key.IsLogical, path, labelX, labelY));
        }

        var maxSelfCount = selfRoutes.Count == 0 ? 0 : selfRoutes.Values.Max();
        var width = nodes.Count == 0 ? 900 : Math.Max(900, nodes.Max(node => node.X + node.Width) + Margin
            + (maxSelfCount == 0 ? 0 : 112 + (maxSelfCount - 1) * 24));
        var height = nodes.Count == 0 ? 520 : Math.Max(520, nodes.Max(node => node.Y + node.Height) + Margin
            + (maxSelfCount == 0 ? 0 : 85 + (maxSelfCount - 1) * 22));
        return new(nodes, edges, width, height, edges.Count(edge => edge.IsLogical), edges.Count(edge => !edge.IsLogical));
    }

    /// <summary>聚焦时把目标表排在左侧、引用本表的表排在右侧，避免多层级折返穿过其他卡片。</summary>
    private static List<RelationshipDiagramNode> ArrangeFocused(List<RelationshipDiagramNode> nodes,
        IReadOnlyList<Relation> relations, TableDesign focusTable)
    {
        var byId = nodes.ToDictionary(node => node.TableId, StringComparer.Ordinal);
        var columnOrder = focusTable.Columns.Select((column, index) => (column.Name, index))
            .ToDictionary(item => item.Name, item => item.index, StringComparer.OrdinalIgnoreCase);
        var leftIds = relations.Where(relation => relation.Source.Id == focusTable.Id && relation.Target.Id != focusTable.Id)
            .GroupBy(relation => relation.Target.Id)
            .OrderBy(group => group.Min(relation => columnOrder.GetValueOrDefault(SqlServerDdl.Names(relation.Key.Columns).FirstOrDefault() ?? "", int.MaxValue)))
            .ThenBy(group => group.First().Target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key).ToList();
        var leftSet = leftIds.ToHashSet(StringComparer.Ordinal);
        var rightIds = relations.Where(relation => relation.Target.Id == focusTable.Id && relation.Source.Id != focusTable.Id
                && !leftSet.Contains(relation.Source.Id))
            .Select(relation => relation.Source).DistinctBy(table => table.Id)
            .OrderBy(table => table.Module, StringComparer.OrdinalIgnoreCase)
            .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Select(table => table.Id).ToList();
        double StackHeight(IEnumerable<string> ids) => ids.Select(id => byId[id].Height + VerticalGap).Sum() - VerticalGap;
        var leftHeight = leftIds.Count == 0 ? 0 : StackHeight(leftIds);
        var rightHeight = rightIds.Count == 0 ? 0 : StackHeight(rightIds);
        var center = byId[focusTable.Id];
        var contentHeight = Math.Max(center.Height, Math.Max(leftHeight, rightHeight));
        var focusX = Margin + (leftIds.Count > 0 ? NodeWidth + 170 : 0);
        var arranged = new Dictionary<string, RelationshipDiagramNode>(StringComparer.Ordinal)
        {
            [focusTable.Id] = center with { X = focusX, Y = Margin + (contentHeight - center.Height) / 2, ContextOnly = false }
        };
        void Place(IReadOnlyList<string> ids, double x, double stackHeight)
        {
            var y = Margin + (contentHeight - stackHeight) / 2;
            foreach (var id in ids)
            {
                var node = byId[id];
                arranged[id] = node with { X = x, Y = y, ContextOnly = false };
                y += node.Height + VerticalGap;
            }
        }
        Place(leftIds, Margin, leftHeight);
        Place(rightIds, focusX + NodeWidth + 170, rightHeight);
        return nodes.Select(node => arranged.GetValueOrDefault(node.TableId, node)).ToList();
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
        RelationshipDiagramNode target, double sourceY, double targetY, int routeIndex, int selfIndex, bool focusedLayout)
    {
        if (source.TableId == target.TableId)
        {
            var right = source.X + source.Width;
            var outer = right + 92 + selfIndex * 24;
            var bottom = Math.Max(source.Y + source.Height + 48, Math.Max(sourceY, targetY) + 82) + selfIndex * 22;
            return ($"M {N(right)} {N(sourceY)} C {N(outer - 22)} {N(sourceY)} {N(outer)} {N(sourceY + 24)} {N(outer)} {N(sourceY + 49)} "
                + $"L {N(outer)} {N(bottom - 24)} Q {N(outer)} {N(bottom)} {N(outer - 24)} {N(bottom)} "
                + $"C {N(right + 22)} {N(bottom)} {N(right + 22)} {N(targetY)} {N(right)} {N(targetY)}",
                outer + 2, (sourceY + bottom) / 2);
        }

        var leftward = source.X >= target.X + target.Width + 40;
        var rightward = target.X >= source.X + source.Width + 40;
        if (leftward || rightward)
        {
            var startX = leftward ? source.X : source.X + source.Width;
            var endX = leftward ? target.X + target.Width : target.X;
            var direction = leftward ? -1 : 1;
            var gap = Math.Abs(endX - startX);
            if (focusedLayout)
            {
                var labelAt = routeIndex % 2 == 0 ? 0.4 : 0.6;
                return ($"M {N(startX)} {N(sourceY)} L {N(endX)} {N(targetY)}",
                    startX + (endX - startX) * labelAt, sourceY + (targetY - sourceY) * labelAt);
            }
            var shift = Math.Clamp(((routeIndex % 7) - 3) * 10.0, -gap / 3, gap / 3);
            var channel = (startX + endX) / 2 + shift;
            if (Math.Abs(sourceY - targetY) < 24)
            {
                return ($"M {N(startX)} {N(sourceY)} C {N(channel)} {N(sourceY)} {N(channel)} {N(targetY)} {N(endX)} {N(targetY)}",
                    channel, (sourceY + targetY) / 2);
            }
            var vertical = Math.Sign(targetY - sourceY);
            const double radius = 12;
            return ($"M {N(startX)} {N(sourceY)} H {N(channel - direction * radius)} "
                + $"Q {N(channel)} {N(sourceY)} {N(channel)} {N(sourceY + vertical * radius)} "
                + $"V {N(targetY - vertical * radius)} Q {N(channel)} {N(targetY)} {N(channel + direction * radius)} {N(targetY)} H {N(endX)}",
                channel, (sourceY + targetY) / 2);
        }

        var sourceRight = source.X + source.Width;
        var targetRight = target.X + target.Width;
        var bend = Math.Max(sourceRight, targetRight) + 50 + routeIndex % 5 * 10;
        return ($"M {N(sourceRight)} {N(sourceY)} C {N(bend)} {N(sourceY)} {N(bend)} {N(targetY)} {N(targetRight)} {N(targetY)}",
            bend, (sourceY + targetY) / 2);
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
