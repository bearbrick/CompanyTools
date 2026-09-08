using System.Security.Claims;
using DbStudio.Core;
using Microsoft.Data.Sqlite;

/// <summary>验证重复保存幂等、完整设计变化及并发保护，不依赖页面的 dirty 标记。</summary>
internal static class RevisionChecks
{
    internal static void Run(StudioStore store, ClaimsPrincipal admin, string dataPath, Action<string, bool> check)
    {
        var project = store.NewProject(admin, "修订号检查", "独立测试项目");
        var table = new TableDesign
        {
            Name = "Versioned",
            Module = "测试模块",
            Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }, new() { Name = "Name", Type = "nvarchar", Length = "40" }]
        };
        project = store.SaveTable(admin, project.Id, project.Revision, table);
        var revision = project.Revision;
        var before = PersistedState();
        for (var i = 0; i < 3; i++)
        {
            project = store.SaveTable(admin, project.Id, project.Revision, ModelJson.Clone(table));
        }
        check("Repeated identical saves preserve revision document and audit", project.Revision == revision && PersistedState() == before);

        var reverted = ModelJson.Clone(table);
        reverted.Columns[1].Length = "80";
        reverted.Columns[1].Length = "40";
        project = store.SaveTable(admin, project.Id, project.Revision, reverted);
        check("Editing then reverting does not create revision", PersistedState() == before);
        project = store.UpdateProject(admin, project.Id, project.Revision, "  " + project.Name + "  ", project.Description);
        project = store.SaveModule(admin, project.Id, project.Revision, table.Module, table.Module);
        check("Unchanged project info and implicit module preserve revision", project.Revision == revision && PersistedState() == before && project.Modules.Count == 0);

        // 反推读取顺序可以不同；同名表必须原位替换，不能凭排序制造结构变化。
        var other = new TableDesign { Name = "Other", Columns = [new() { Name = "Id", Type = "int" }] };
        project = store.SaveTable(admin, project.Id, project.Revision, other);
        before = PersistedState();
        var reverse = ModelJson.Clone(project);
        reverse.Tables.Reverse();
        project = store.ApplyDatabaseSnapshot(admin, project.Id, project.Revision, new(reverse, []));
        check("Identical reverse snapshot preserves table order and audit", PersistedState() == before && project.Tables[0].Id == table.Id);

        AssertChange("Column type detail changes create one revision", t => t.Columns[1].Length = "80");
        AssertChange("Design comments create one revision", t => t.Columns[1].Comment = "用于业务检索");
        AssertChange("Index changes create one revision", t => t.Indexes.Add(new() { Name = "IX_Versioned_Name", Columns = "Name" }));
        AssertChange("Column order changes create one revision", t => t.Columns.Reverse());
        AssertChange("Foreign key changes create one revision", t => t.ForeignKeys.Add(new() { Name = "FK_Versioned_Self", Columns = "Id", TargetTableId = table.Id, TargetColumns = "Id" }));

        before = PersistedState();
        var staleRejected = false;
        try
        {
            store.SaveTable(admin, project.Id, project.Revision - 1, table);
        }
        catch (InvalidOperationException) { staleRejected = true; }
        check("Stale identical save still rejects without writing", staleRejected && PersistedState() == before);

        void AssertChange(string name, Action<TableDesign> mutate)
        {
            var previousRevision = project.Revision;
            var previousAuditCount = store.Audit(admin, project.Id).Count;
            mutate(table);
            project = store.SaveTable(admin, project.Id, project.Revision, table);
            var afterChange = PersistedState();
            project = store.SaveTable(admin, project.Id, project.Revision, ModelJson.Clone(table));
            check(name, project.Revision == previousRevision + 1 && store.Audit(admin, project.Id).Count == previousAuditCount + 1 && PersistedState() == afterChange);
        }

        (long Revision, string Document, long AuditCount) PersistedState()
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataPath, "studio.db") }.ConnectionString);
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Revision,Document,(SELECT COUNT(*) FROM Audit WHERE ProjectId=$id) FROM Projects WHERE Id=$id";
            command.Parameters.AddWithValue("$id", project.Id);
            using var row = command.ExecuteReader();
            row.Read();
            return (row.GetInt64(0), row.GetString(1), row.GetInt64(2));
        }
    }
}
