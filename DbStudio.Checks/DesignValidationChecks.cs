using System.Security.Claims;
using DbStudio.Core;

internal static class DesignValidationChecks
{
    internal static void Run(StudioStore store, ClaimsPrincipal admin, Action<string, bool> check)
    {
        var project = store.NewProject(admin, "字段定义必填检查", "隔离测试");
        var table = new TableDesign { Name = "RequiredLabels", Columns = [new() { Name = "Id", Type = "int", Label = "编号" }, new() { Name = "Column2", Label = "  " }] };
        var issues = DesignValidation.Check(project, table);
        check("Design check identifies missing label by row and field", issues.Any(i => i.Contains("第 2 行") && i.Contains("Column2") && i.Contains("定义名必填")));
        foreach (var label in new[] { "", " \t\n" })
        {
            table.Columns[1].Label = label;
            var rejected = false;
            try { store.SaveTable(admin, project.Id, project.Revision, table); }
            catch (InvalidOperationException e) when (e.Message.Contains("定义名必填")) { rejected = true; }
            check("Server rejects empty or whitespace definition without saving", rejected && store.Projects(admin).Single(p => p.Id == project.Id).Revision == project.Revision);
        }
        table.Columns[1].Label = "新增字段";
        check("Filled definitions pass shared check", DesignValidation.Check(project, table).Count == 0);
        project = store.SaveTable(admin, project.Id, project.Revision, table);
        check("Valid definition saves once", project.Tables.Single().Columns[1].Label == "新增字段");
        var revision = project.Revision;
        table.Columns[0].Label = "";
        try { store.SaveTable(admin, project.Id, revision, table); }
        catch (InvalidOperationException) { }
        var saved = store.Projects(admin).Single(p => p.Id == project.Id);
        check("Failed edit preserves revision and saved definition", saved.Revision == revision && saved.Tables[0].Columns[0].Label == "编号");
        check("Historical SQL metadata remains readable without labels", SqlServerDdl.Validate(project, table).Count == 0);
    }
}
