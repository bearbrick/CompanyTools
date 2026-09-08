using DbStudio.Core;

/// <summary>复现尚未落库的单表被无关整库变化误拦，并验证真正的表/依赖变化仍会停止。</summary>
internal static class SchemaDriftChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var existing = new TableDesign { Name = "Existing", Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }] };
        var fresh = new TableDesign { Name = "NotCreatedYet", Columns = [new() { Name = "Id", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }] };
        var design = new DesignProject { Tables = [existing, fresh] };
        var actual = new DesignProject { Tables = [ModelJson.Clone(existing)] };
        var before = SqlServerTools.BuildPackage(actual);
        actual.Tables[0].Columns.Add(new() { Name = "Unrelated", Type = "int" });
        var current = SqlServerTools.BuildPackage(actual);
        var scope = new TableDeploymentScope(fresh.Id, fresh.Schema, fresh.Name);
        check("Missing-table preview ignores unrelated table changes", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, scope).Count == 0);
        check("Whole-project drift still detects another table change", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, null).Count > 0);
        fresh.ForeignKeys.Add(new() { Name = "FK_New_Existing", Columns = "Id", TargetTableId = existing.Id, TargetColumns = "Id" });
        check("Missing-table preview checks its planned foreign key target", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, scope).Count > 0);
        fresh.ForeignKeys.Clear();
        actual.Tables.Add(ModelJson.Clone(fresh));
        current = SqlServerTools.BuildPackage(actual);
        check("Missing table created by another session is real drift", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, scope).Any(c => c.Name.Contains(fresh.Name)));
        before = current;
        actual.Tables[1].Columns.Add(new() { Name = "ConcurrentColumn", Type = "int" });
        current = SqlServerTools.BuildPackage(actual);
        check("Selected table concurrent field change remains blocked", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, scope).Count > 0);
        before = current;
        actual.Tables[1].Label = "Actual changed description";
        current = SqlServerTools.BuildPackage(actual);
        check("Selected table description change remains visible", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, scope).Count > 0);
        check("Selected table description removal remains visible", SchemaDriftCheck.FindChanges(current, before, "DriftTest", design, scope).Count > 0);
        check("Identical snapshots are semantically stable", SchemaDriftCheck.FindChanges(current, current, "DriftTest", design, scope).Count == 0);
        var semantic = ModelJson.Clone(actual);
        semantic.Tables[1].Columns[0].Default = "1";
        var one = SqlServerTools.BuildPackage(semantic);
        semantic.Tables[1].Columns[0].Default = "((1))";
        var two = SqlServerTools.BuildPackage(semantic);
        check("Equivalent SQL expression representation does not cause drift", SchemaDriftCheck.FindChanges(one, two, "DriftTest", design, scope).Count == 0);
        actual.Tables[1].ForeignKeys.Add(new() { Name = "FK_Actual_Outgoing", Columns = "Id", TargetColumns = "Id", TargetTableId = existing.Id });
        before = SqlServerTools.BuildPackage(actual);
        actual.Tables[0].Columns.Add(new() { Name = "DependencyChange", Type = "int" });
        current = SqlServerTools.BuildPackage(actual);
        check("Actual outgoing foreign key dependencies are checked even when missing from design", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, scope).Count > 0);
        actual.Tables[1].ForeignKeys.Clear();
        actual.Tables[0].ForeignKeys.Add(new() { Name = "FK_Actual_Incoming", Columns = "Id", TargetColumns = "Id", TargetTableId = fresh.Id });
        before = SqlServerTools.BuildPackage(actual);
        actual.Tables[0].Columns.Add(new() { Name = "IncomingChange", Type = "int" });
        current = SqlServerTools.BuildPackage(actual);
        check("Actual incoming foreign key dependencies remain protected", SchemaDriftCheck.FindChanges(before, current, "DriftTest", design, scope).Count > 0);
    }
}
