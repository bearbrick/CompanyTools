using System.Text.Json;
using DbStudio.Core;

/// <summary>ER 图布局必须区分逻辑与物理关系、保留筛选上下文，并且不能改写项目快照。</summary>
internal static class RelationshipDiagramChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var parent = new TableDesign
        {
            Id = "parent",
            Name = "Customer",
            Label = "客户",
            Module = "基础资料",
            Columns =
            [
                new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                new() { Name = "Name", Type = "nvarchar", Length = "80" }
            ]
        };
        var logicalChild = new TableDesign
        {
            Id = "order",
            Name = "SalesOrder",
            Label = "销售订单",
            Module = "销售",
            Columns =
            [
                new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                new() { Name = "CustomerId", Type = "int" },
                new() { Name = "Remark", Type = "nvarchar", Length = "200" }
            ],
            ForeignKeys = [new() { Name = "FK_Order_Customer", IsLogical = true, Columns = "CustomerId", TargetTableId = parent.Id, TargetColumns = "Id" }]
        };
        var physicalChild = new TableDesign
        {
            Id = "contact",
            Name = "Contact",
            Label = "联系人",
            Module = "基础资料",
            Columns =
            [
                new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                new() { Name = "CustomerId", Type = "int" }
            ],
            ForeignKeys = [new() { Name = "FK_Contact_Customer", Columns = "CustomerId", TargetTableId = parent.Id, TargetColumns = "Id" }]
        };
        var orphan = new TableDesign
        {
            Id = "orphan",
            Name = "SystemSetting",
            Label = "系统配置",
            Module = "系统",
            Columns = [new() { Name = "Id", Type = "int" }]
        };
        var project = new DesignProject { Name = "ER 检查", Tables = [parent, logicalChild, physicalChild, orphan] };
        var original = JsonSerializer.Serialize(project, ModelJson.Options);

        var model = RelationshipDiagramLayout.Build(project);
        check("ER diagram defaults to related tables and distinguishes relationship types",
            model.Nodes.Count == 3 && model.Edges.Count == 2 && model.LogicalCount == 1 && model.PhysicalCount == 1
            && model.Edges.Any(edge => edge.IsLogical) && model.Edges.Any(edge => !edge.IsLogical));
        var parentNode = model.Nodes.Single(node => node.TableId == parent.Id);
        var orderNode = model.Nodes.Single(node => node.TableId == logicalChild.Id);
        check("ER diagram places referenced tables before their children and emits paths",
            parentNode.X < orderNode.X && model.Edges.All(edge => edge.Path.StartsWith("M ") && edge.Path.Contains(" C ")));
        check("Compact ER nodes include primary and relationship fields but omit ordinary payload",
            orderNode.Columns.Any(column => column.Name == "Id" && column.PrimaryKey)
            && orderNode.Columns.Any(column => column.Name == "CustomerId" && column.RelationshipKey)
            && orderNode.Columns.All(column => column.Name != "Remark"));

        var expanded = RelationshipDiagramLayout.Build(project, onlyRelated: false, showAllColumns: true);
        check("Expanded ER view includes unrelated tables and ordinary fields",
            expanded.Nodes.Count == 4 && expanded.Nodes.Single(node => node.TableId == logicalChild.Id).Columns.Any(column => column.Name == "Remark"));
        var unconnectedProject = new DesignProject
        {
            Tables = Enumerable.Range(1, 10).Select(index => new TableDesign
            {
                Id = $"standalone-{index}",
                Name = $"Standalone{index}",
                Columns = [new() { Name = "Id", Type = "int", PrimaryKeyOrder = 1 }]
            }).ToList()
        };
        var unconnected = RelationshipDiagramLayout.Build(unconnectedProject, onlyRelated: false);
        check("Unconnected ER tables use a balanced grid instead of one compressed column",
            unconnected.Nodes.Select(node => node.X).Distinct().Count() > 1
            && unconnected.Nodes.Select(node => node.Y).Distinct().Count() > 1);
        var logical = RelationshipDiagramLayout.Build(project, relationType: "logical");
        check("ER relationship type filter removes physical edges and unrelated endpoints",
            logical.Nodes.Select(node => node.TableId).ToHashSet().SetEquals([parent.Id, logicalChild.Id])
            && logical.Edges.Count == 1 && logical.LogicalCount == 1 && logical.PhysicalCount == 0);
        var searched = RelationshipDiagramLayout.Build(project, search: "销售订单");
        check("ER search retains one-hop relationship context and marks it as context only",
            searched.Nodes.Select(node => node.TableId).ToHashSet().SetEquals([parent.Id, logicalChild.Id])
            && searched.Nodes.Single(node => node.TableId == parent.Id).ContextOnly
            && !searched.Nodes.Single(node => node.TableId == logicalChild.Id).ContextOnly);
        var module = RelationshipDiagramLayout.Build(project, module: "基础资料");
        check("ER module filter keeps direct cross-module relations visible",
            module.Nodes.Any(node => node.TableId == logicalChild.Id && node.ContextOnly)
            && module.Nodes.Any(node => node.TableId == parent.Id && !node.ContextOnly));
        check("ER layout never mutates the saved project snapshot", JsonSerializer.Serialize(project, ModelJson.Options) == original);

        var invalidRejected = false;
        try
        {
            RelationshipDiagramLayout.Build(project, relationType: "unknown");
        }
        catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        check("ER layout rejects unknown relationship filters", invalidRejected);
    }
}
