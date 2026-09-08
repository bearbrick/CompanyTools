using System.Security.Claims;
using System.Text.Json;
using DbStudio.Core;

/// <summary>模块排序仅改变导航元数据，保留表结构，并遵守项目权限、修订号和原子保存规则。</summary>
internal static class ModuleOrderChecks
{
    internal static void Run(StudioStore store, ClaimsPrincipal admin, ClaimsPrincipal outsider, Action<string, bool> check)
    {
        var project = store.NewProject(admin, "模块排序验证", "隔离测试项目");
        var table = new TableDesign { Name = "ModuleTable", Module = "隐式模块", Columns = [new() { Name = "Id", Type = "int" }] };
        project = store.SaveTable(admin, project.Id, project.Revision, table);
        var revision = project.Revision;
        project = store.SaveModuleOrder(admin, project.Id, revision, ["隐式模块"]);
        check("Unchanged implicit module order does not materialize metadata or bump revision", project.Modules.Count == 0 && project.Revision == revision);
        project = store.SaveModule(admin, project.Id, project.Revision, null, "空模块");
        project = store.SaveModule(admin, project.Id, project.Revision, null, "末尾模块");
        var beforeTables = JsonSerializer.Serialize(project.Tables, ModelJson.Options);
        var beforeAudit = store.Audit(admin, project.Id).Count;
        revision = project.Revision;
        project = store.SaveModuleOrder(admin, project.Id, revision, ["末尾模块", "隐式模块", "空模块"]);
        check("Module order persists including empty modules with one revision and audit", project.Modules.SequenceEqual(["末尾模块", "隐式模块", "空模块"])
            && project.Revision == revision + 1 && store.Audit(admin, project.Id).Count == beforeAudit + 1);
        check("Sorting modules does not modify tables", beforeTables == JsonSerializer.Serialize(project.Tables, ModelJson.Options));
        var reloaded = store.Projects(admin).Single(p => p.Id == project.Id);
        check("Module order survives reload and backup round trip", ModuleOrdering.Names(ModelJson.Clone(reloaded)).SequenceEqual(project.Modules));
        revision = project.Revision;
        project = store.SaveModuleOrder(admin, project.Id, revision, project.Modules);
        check("Repeated module order is a no-op", project.Revision == revision && store.Audit(admin, project.Id).Count == beforeAudit + 1);
        check("Invalid module permutations are rejected", Reject(() => store.SaveModuleOrder(admin, project.Id, revision, ["末尾模块", "隐式模块"]))
            && Reject(() => store.SaveModuleOrder(admin, project.Id, revision, ["末尾模块", "末尾模块", "空模块"]))
            && Reject(() => store.SaveModuleOrder(admin, project.Id, revision, ["陌生模块", "隐式模块", "空模块"])));
        check("Stale sorting cannot overwrite current module order", Reject(() => store.SaveModuleOrder(admin, project.Id, revision - 1, project.Modules)));
        var denied = false;
        try { store.SaveModuleOrder(outsider, project.Id, revision, project.Modules); }
        catch (UnauthorizedAccessException) { denied = true; }
        check("Module ordering requires project design permission", denied);
        check("Rejected sorting does not write a revision", store.Projects(admin).Single(p => p.Id == project.Id).Revision == revision);
        project = store.SaveModule(admin, project.Id, revision, "隐式模块", "业务模块");
        check("Renaming a module keeps its position and moves member tables", ModuleOrdering.Names(project).SequenceEqual(["末尾模块", "业务模块", "空模块"])
            && project.Tables.Single().Module == "业务模块");
        project = store.SaveModule(admin, project.Id, project.Revision, null, "新增模块");
        check("New module appends after saved order", ModuleOrdering.Names(project).Last() == "新增模块");
    }

    /// <summary>预期的输入或版本拒绝不应转化成成功。</summary>
    private static bool Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }
}
