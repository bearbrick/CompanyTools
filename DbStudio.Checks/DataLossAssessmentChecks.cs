using DbStudio.Core;

/// <summary>验证高风险 DDL 的结构化识别、部署选项和确认边界。</summary>
internal static class DataLossAssessmentChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var warnings = new List<DeploymentWarning>
        {
            new("DataIssue", "数据兼容性风险", "", [new("7", "现有 NULL 值无法改为 NOT NULL", [])], "")
        };
        var risks = DataLossAssessment.Assess(
            [new("Drop", "SqlTable", "[dbo].[Orders]"), new("Drop", "SqlSimpleColumn", "[dbo].[Users].[Legacy]")],
            warnings, ["[dbo].[Users].[Name]：nvarchar(100) → nvarchar(20)"]);
        check("Data loss assessment lists table column capacity and compatibility risks",
            risks.Select(risk => risk.Code).ToHashSet().SetEquals(["DropTable", "DropColumn", "TypeCapacityReduction", "DataIssue"]));
        check("Data loss confirmation is bound to the exact database",
            DataLossAssessment.Confirmation("ProductDb") == "允许数据损失 ProductDb"
            && DataLossAssessment.Confirmation("TestDb") != DataLossAssessment.Confirmation("ProductDb"));
        var target = new DesignProject { Tables = [new() { Name = "Users", Columns = [new() { Name = "Id", Type = "int" }, new() { Name = "Legacy", Type = "int" }] }] };
        var source = ModelJson.Clone(target);
        source.Tables[0].Columns.RemoveAt(1);
        var modelRisks = DataLossAssessment.Assess(SqlServerTools.BuildPackage(source), SqlServerTools.BuildPackage(target), false, [], [], []);
        check("Model comparison finds a removed column even when DacFx suppresses its warning",
            modelRisks.Single().Code == "DropColumn" && modelRisks[0].Detail.Contains("Legacy"));

        var protectedOptions = SchemaDeploymentOptions.Create(prune: true, allowDataLoss: false);
        var unlockedOptions = SchemaDeploymentOptions.Create(prune: true, allowDataLoss: true);
        check("DDL policy blocks possible loss by default and unlocks it explicitly",
            protectedOptions.BlockOnPossibleDataLoss && !unlockedOptions.BlockOnPossibleDataLoss);
        check("DDL policy permits transactional table recreation without inventing data",
            unlockedOptions.AllowTableRecreation && unlockedOptions.IncludeTransactionalScripts
            && !unlockedOptions.GenerateSmartDefaults && !unlockedOptions.AllowUnsafeRowLevelSecurityDataMovement);
    }
}
