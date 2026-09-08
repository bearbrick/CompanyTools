using System.Xml.Linq;
using DbStudio.Core;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

/// <summary>
/// 使用独立编写的目标 DDL 校验约束身份，避免两边经过相同生成器掩盖元数据遗漏。
/// 全部比较在内存中进行，不连接或修改 SQL Server。
/// </summary>
internal static class ConstraintIdentityChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var table = new TableDesign
        {
            Name = "ConstraintSample", Label = "",
            PrimaryKeyName = "PK__ConstraintSample__Original", PrimaryKeySystemNamed = true,
            Columns =
            [
                new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 },
                new() { Name = "Status", Type = "int", Nullable = false, Default = "((1))", DefaultConstraintName = "DF_ConstraintSample_Status" },
                new() { Name = "AutoValue", Type = "int", Default = "((0))", DefaultConstraintName = "DF__ConstraintSample__Auto", DefaultConstraintSystemNamed = true }
            ]
        };
        var project = new DesignProject { Tables = [table] };
        using var targetModel = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        targetModel.AddObjects("""
            CREATE TABLE dbo.ConstraintSample (
                Id int NOT NULL PRIMARY KEY CLUSTERED,
                Status int NOT NULL CONSTRAINT DF_ConstraintSample_Status DEFAULT ((1)),
                AutoValue int NULL DEFAULT ((0))
            );
            """);
        using var targetBytes = new MemoryStream();
        DacPackageExtensions.BuildPackage(targetBytes, targetModel, new PackageMetadata { Name = "IndependentConstraintTarget", Version = "1.0.0.0" });
        targetBytes.Position = 0;
        using var target = DacPackage.Load(targetBytes);

        List<(string Operation, string Type)> Compare(DesignProject design)
        {
            using var sourceBytes = new MemoryStream(SqlServerTools.BuildPackage(design));
            using var source = DacPackage.Load(sourceBytes);
            var report = DacServices.GenerateDeployReport(source, target, "ConstraintCheck", new DacDeployOptions { BlockOnPossibleDataLoss = true });
            return XDocument.Parse(report).Descendants().Where(e => e.Name.LocalName == "Operation")
                .SelectMany(op => op.Descendants().Where(e => e.Name.LocalName == "Item")
                    .Select(item => ((string)op.Attribute("Name")!, (string)item.Attribute("Type")!))).ToList();
        }

        check("Named defaults and system-named PK round-trip against independent DDL", Compare(project).Count == 0);
        var lostName = ModelJson.Clone(project);
        lostName.Tables[0].Columns[1].DefaultConstraintName = "";
        check("Missing default constraint name reproduces drop and create", Compare(lostName).Count(i => i.Type == "SqlDefaultConstraint") == 2);
        var changedDefault = ModelJson.Clone(project);
        changedDefault.Tables[0].Columns[1].Default = "2";
        check("Real default value changes remain visible", Compare(changedDefault).Any(i => i.Type == "SqlDefaultConstraint"));
        var removedDefault = ModelJson.Clone(project);
        removedDefault.Tables[0].Columns[1].Default = "";
        check("Removing a default still produces a drop", Compare(removedDefault).Any(i => i.Type == "SqlDefaultConstraint" && i.Operation == "Drop"));
        var changedPk = ModelJson.Clone(project);
        changedPk.Tables[0].Columns[1].PrimaryKeyOrder = 2;
        // 聚集主键改变可能表现为表重建，由 DacFx 决定具体部署操作。
        check("Real primary key changes remain visible", Compare(changedPk).Any(i => i.Type is "SqlPrimaryKeyConstraint" or "SqlTable"));

        var copied = DesignEditing.CopyTable(project, table);
        check("Copy clears inherited constraint identities", !copied.PrimaryKeySystemNamed && copied.Columns.All(c => c.DefaultConstraintName == "" && !c.DefaultConstraintSystemNamed));
        check("Copy keeps default expressions", copied.Columns.Select(c => c.Default).SequenceEqual(table.Columns.Select(c => c.Default)));
        var invalid = ModelJson.Clone(project);
        invalid.Tables[0].Columns[1].DefaultConstraintName = new string('x', 129);
        check("Invalid default constraint names rejected", SqlServerDdl.Validate(invalid, invalid.Tables[0]).Count > 0);
        var duplicate = ModelJson.Clone(project);
        duplicate.Tables[0].Columns[2].DefaultConstraintSystemNamed = false;
        duplicate.Tables[0].Columns[2].DefaultConstraintName = table.Columns[1].DefaultConstraintName;
        check("Duplicate default constraint names rejected", SqlServerDdl.Validate(duplicate, duplicate.Tables[0]).Count > 0);
        var quoted = ModelJson.Clone(project);
        quoted.Tables[0].Columns[1].DefaultConstraintName = "DF_Status]Quote";
        check("Default constraint identifiers escaped", SqlServerDdl.Generate(quoted, quoted.Tables[0]).Contains("CONSTRAINT [DF_Status]]Quote] DEFAULT"));
        check("Backup round-trip retains constraint metadata", ModelJson.Clone(project).Tables[0].Columns[1].DefaultConstraintName == "DF_ConstraintSample_Status"
            && ModelJson.Clone(project).Tables[0].PrimaryKeySystemNamed);
    }
}
