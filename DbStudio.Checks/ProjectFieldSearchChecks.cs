using System.Text.Json;
using DbStudio.Core;

/// <summary>顶部字段索引必须覆盖文本、同名字段及双向关系，并保持项目快照只读。</summary>
internal static class ProjectFieldSearchChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var customer = new TableDesign
        {
            Id = "customer",
            Name = "Customer",
            Label = "客户",
            Module = "基础资料",
            Columns =
            [
                new() { Id = "customer-id", Name = "Id", Label = "客户主键", Type = "bigint", PrimaryKeyOrder = 1 },
                new() { Id = "customer-code", Name = "Code", Label = "客户编号", Type = "nvarchar", Length = "20" }
            ]
        };
        var order = new TableDesign
        {
            Id = "order",
            Name = "SalesOrder",
            Label = "销售订单",
            Module = "销售",
            Columns =
            [
                new() { Id = "order-id", Name = "Id", Label = "订单主键", Type = "bigint", PrimaryKeyOrder = 1 },
                new() { Id = "order-customer", Name = "CustomerId", Label = "客户ID", Type = "bigint", Comment = "下单客户" }
            ],
            ForeignKeys = [new() { Name = "LR_Order_Customer", IsLogical = true, Columns = "CustomerId", TargetTableId = customer.Id, TargetColumns = "Id" }]
        };
        var invoice = new TableDesign
        {
            Id = "invoice",
            Name = "Invoice",
            Label = "发票",
            Module = "财务",
            Columns = [new() { Id = "invoice-customer", Name = "CustomerId", Label = "客户ID", Type = "bigint" }],
            ForeignKeys = [new() { Name = "FK_Invoice_Customer", Columns = "CustomerId", TargetTableId = customer.Id, TargetColumns = "Id" }]
        };
        var project = new DesignProject { Tables = [customer, order, invoice] };
        var original = JsonSerializer.Serialize(project, ModelJson.Options);
        var index = ProjectFieldSearchIndex.Build(project);

        var exact = index.Search("CustomerId");
        check("Field search finds same-name fields and exact names rank first",
            exact.Total == 2 && exact.Items.All(item => item.ColumnName == "CustomerId" && item.SameNameCount == 2));
        var logical = exact.Items.Single(item => item.TableId == order.Id);
        check("Field search exposes logical outgoing references",
            logical.References.Count == 1 && logical.References[0].TableId == customer.Id && logical.References[0].IsLogical);
        var parent = index.Search("客户主键").Items.Single();
        check("Field search builds inbound references across logical and physical relations",
            parent.ReferencedBy.Count == 2 && parent.ReferencedBy.Any(item => item.IsLogical)
            && parent.ReferencedBy.Any(item => !item.IsLogical));
        check("Field search covers Chinese definitions table names and comments",
            index.Search("销售订单").Items.Count == 2 && index.Search("下单客户").Items.Single().ColumnId == "order-customer");
        check("Field search supports multi-term narrowing",
            index.Search("销售 CustomerId").Items.Single().TableId == order.Id);
        check("Field search reports totals before result limiting",
            index.Search("bigint", 2) is { Total: 4, Items.Count: 2 });
        check("Field search returns no rows for blank input", index.Search("  ").Total == 0);

        var invalidLimitRejected = false;
        try
        {
            index.Search("Id", 0);
        }
        catch (ArgumentOutOfRangeException) { invalidLimitRejected = true; }
        check("Field search rejects invalid result limits", invalidLimitRejected);
        check("Field search indexing never mutates the project snapshot",
            JsonSerializer.Serialize(project, ModelJson.Options) == original);
    }
}
