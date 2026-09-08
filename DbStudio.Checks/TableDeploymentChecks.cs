using System.Xml.Linq;
using DbStudio.Core;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

/// <summary>用独立 DacFx 包验证单表部署的范围隔离及完整报告检查，不连接业务数据库。</summary>
internal static class TableDeploymentChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var a = new TableDesign
        {
            Name = "Scoped", Label = "单表", PrimaryKeyName = "PK_Scoped",
            Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                new() { Name = "Name", Type = "nvarchar", Length = "40", Label = "名称" },
                new() { Name = "Amount", Type = "int", Default = "0", DefaultConstraintName = "DF_Scoped_Amount" }],
            Indexes = [new() { Name = "IX_Scoped_Name", Columns = "Name" }],
            Checks = [new() { Name = "CK_Scoped_Amount", Expression = "[Amount] >= 0" }]
        };
        var b = new TableDesign { Name = "Other", Label = "其他表", Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }] };
        var project = new DesignProject { Tables = [a, b] };
        a.ForeignKeys.Add(new() { Name = "FK_Scoped_Other", Columns = "Id", TargetColumns = "Id", TargetTableId = b.Id });
        var scope = new TableDeploymentScope(a.Id, a.Schema, a.Name);
        var baseline = SqlServerTools.BuildPackage(project);
        using var input = new MemoryStream(baseline);
        using var model = TSqlModel.LoadFromDacpac(input, new ModelLoadOptions());
        model.AddObjects("""
            CREATE VIEW dbo.OtherView AS SELECT Id FROM dbo.Other;
            GO
            CREATE TRIGGER dbo.TR_Scoped ON dbo.Scoped AFTER INSERT AS PRINT 'preserved';
            GO
            EXEC sys.sp_addextendedproperty @name=N'CustomProperty',@value=N'保留',@level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'Scoped';
            GO
            """);
        using var buffer = new MemoryStream();
        DacPackageExtensions.BuildPackage(buffer, model, new PackageMetadata { Name = "Target" });
        var target = buffer.ToArray();
        var identical = TableDeployment.BuildPackage(project, scope, target);
        check("Single-table model overlay has zero diff for unchanged design", Compare(identical).Count == 0);

        // 未选择表包含待发布甚至不合法的新设计，不应进入当前表的部署包。
        b.Columns.Add(new() { Name = "UnrelatedNewField", Type = "int" });
        project.Tables.Add(new() { Name = "DesignOnly", Columns = [new() { Type = "unsupported" }] });
        a.Columns[1].Length = "80";
        var source = TableDeployment.BuildPackage(project, scope, target);
        var changes = Compare(source);
        TableDeployment.ValidateChanges(source, target, scope, changes);
        check("Single-table diff contains the selected field change", changes.Any(change => change.Name.Contains("Scoped")));
        check("Other table edits and design-only tables are excluded", changes.All(change => !change.Name.Contains("Other") && !change.Name.Contains("DesignOnly")));
        using var sourceInput = new MemoryStream(source);
        using var scopedModel = TSqlModel.LoadFromDacpac(sourceInput, new ModelLoadOptions());
        check("Single-table package retains target views triggers and custom annotations", scopedModel.GetObjects(DacQueryScopes.UserDefined, View.TypeClass).Any()
            && scopedModel.GetObjects(DacQueryScopes.UserDefined, DmlTrigger.TypeClass).Any()
            && scopedModel.GetObjects(DacQueryScopes.UserDefined, ExtendedProperty.TypeClass).Any(item => item.Name.Parts.Last() == "CustomProperty"));

        a.Columns[2].Default = "1";
        a.Columns[1].Label = "新名称";
        a.Indexes.Clear();
        a.Checks.Clear();
        a.ForeignKeys.Clear();
        source = TableDeployment.BuildPackage(project, scope, target);
        changes = Compare(source);
        TableDeployment.ValidateChanges(source, target, scope, changes);
        check("Single-table diff includes selected defaults annotations and constraint removals", changes.Any(c => c.ObjectType == "SqlDefaultConstraint")
            && changes.Any(c => c.ObjectType == "SqlExtendedProperty") && changes.Any(c => c.ObjectType == "SqlIndex" && c.Operation == "Drop")
            && changes.Any(c => c.ObjectType == "SqlForeignKeyConstraint" && c.Operation == "Drop") && changes.Any(c => c.ObjectType == "SqlCheckConstraint" && c.Operation == "Drop"));
        check("Scope guard rejects changes to another table", Reject(() => TableDeployment.ValidateChanges(source, target, scope, [new("Alter", "SqlTable", "[dbo].[Other]")])));
        check("Scope guard rejects unknown deployment objects", Reject(() => TableDeployment.ValidateChanges(source, target, scope, [new("Drop", "SqlView", "[dbo].[OtherView]")])));
        check("Missing selected table never falls back to whole database", Reject(() => TableDeployment.BuildPackage(project, scope with { TableId = "missing" }, target)));

        List<SchemaChange> Compare(byte[] bytes)
        {
            using var sourceStream = new MemoryStream(bytes);
            using var sourcePackage = DacPackage.Load(sourceStream);
            using var targetStream = new MemoryStream(target);
            using var targetPackage = DacPackage.Load(targetStream);
            var report = XDocument.Parse(DacServices.GenerateDeployReport(sourcePackage, targetPackage, "ScopedTest", new DacDeployOptions
            {
                DropObjectsNotInSource = true, DropConstraintsNotInSource = true, DropIndexesNotInSource = true,
                ScriptDatabaseOptions = false, IgnorePermissions = true, IgnoreColumnOrder = true
            }));
            return report.Descendants().Where(e => e.Name.LocalName == "Operation")
                .SelectMany(operation => operation.Descendants().Where(e => e.Name.LocalName == "Item")
                    .Select(item => new SchemaChange((string?)operation.Attribute("Name") ?? "", (string?)item.Attribute("Type") ?? "", (string?)item.Attribute("Value") ?? ""))).ToList();
        }
    }

    private static bool Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }
}
