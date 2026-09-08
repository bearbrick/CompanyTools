using System.Security.Claims;
using System.Text.Json;
using DbStudio.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Permission = DbStudio.Core.Permission;

int passed = 0;
void Check(string name, bool value) { if (!value) { throw new Exception("FAIL: " + name); } Console.WriteLine("PASS: " + name); passed++; }
void Reject<T>(string name, Action action) where T : Exception { try { action(); throw new Exception("FAIL: " + name + " was allowed"); } catch (T) { Check(name, true); } }
// 从输出目录逐级查找源项目，兼容常规构建和独立预览输出目录。
var sourceDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (sourceDirectory != null && !File.Exists(Path.Combine(sourceDirectory.FullName, "DbStudio", "Seed", "panqi.json")))
{
    sourceDirectory = sourceDirectory.Parent;
}
var root = sourceDirectory == null
    ? throw new DirectoryNotFoundException("未找到 DbStudio/Seed/panqi.json，请从解决方案目录运行检查。")
    : Path.Combine(sourceDirectory.FullName, "DbStudio");
var testPath = Path.Combine(Path.GetTempPath(), "DbStudioChecks-" + Guid.NewGuid().ToString("N"));
var env = new TestEnvironment { ContentRootPath = root };
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:DataDirectory"] = testPath, ["Studio:InitialPassword"] = "TestAdmin12345!", ["Studio:LoadLegacySeed"] = "true" }).Build();
var store = new StudioStore(env, config);
var admin = store.Login("admin", "TestAdmin12345!")!;
Check("Admin login", admin != null);
Check("Wrong password rejected", store.Login("admin", "wrong") == null);
Reject<UnauthorizedAccessException>("Anonymous read rejected", () => store.Projects(new ClaimsPrincipal()));
var imported = store.Projects(admin!)[0];
Check("Source import count", imported.Tables.Count == 99 && imported.Tables.Sum(t => t.Columns.Count) == 1778);
var customer = imported.Tables.First(t => t.Name == "ERP_Sales_Customer");
var customerSql = store.Ddl(admin!, imported, customer);
Check("Customer source types retained", customerSql.Contains("[CustNum] nvarchar(20) NOT NULL") && customerSql.Contains("[RespDeptId] uniqueidentifier"));

var project = store.NewProject(admin!, "测试项目", "自动化测试，隔离数据目录");
var parent = new TableDesign { Name = "Parent", Label = "父表", Columns = [new() { Name = "TenantId", Type = "int", Nullable = false, PrimaryKeyOrder = 1 }, new() { Name = "Id", Type = "bigint", Nullable = false, PrimaryKeyOrder = 2 }] };
project = store.SaveTable(admin!, project.Id, project.Revision, parent);
var child = new TableDesign
{
    Name = "Child",
    Label = "子表",
    Comment = "包含 ' 引号",
    Columns = [new() { Name = "Id", Type = "bigint", Nullable = false, PrimaryKeyOrder = 1, Identity = true }, new() { Name = "TenantId", Type = "int", Nullable = false }, new() { Name = "ParentId", Type = "bigint", Nullable = false }, new() { Name = "Price", Type = "decimal", Precision = 18, Scale = 2, Nullable = false, Default = "0" }, new() { Name = "Quantity", Type = "int", Nullable = false, Default = "1" }, new() { Name = "Total", Type = "decimal", Computed = "[Price] * [Quantity]", Persisted = true }, new() { Name = "Name", Type = "nvarchar", Length = "100", Default = "N'未命名'" }],
    ForeignKeys = [new() { Name = "FK_Child_Parent", Columns = "TenantId, ParentId", TargetTableId = parent.Id, TargetColumns = "TenantId,Id" }],
    Indexes = [new() { Name = "IX_Child_Parent", Columns = "TenantId,ParentId", Include = "Price", Filter = "[Quantity] > 0" }, new() { Name = "UQ_Child_Name", Columns = "Name", Unique = true, IsConstraint = true }],
    Checks = [new() { Name = "CK_Child_Quantity", Expression = "[Quantity] > 0" }]
};
project = store.SaveTable(admin!, project.Id, project.Revision, child);
var ddl = store.Ddl(admin!, project, child);
Check("Identity generated", ddl.Contains("IDENTITY(1,1)"));
Check("Computed persisted generated", ddl.Contains("[Total] AS ([Price] * [Quantity]) PERSISTED"));
Check("Composite FK order", ddl.Contains("FOREIGN KEY ([TenantId], [ParentId]) REFERENCES [dbo].[Parent] ([TenantId], [Id])"));
Check("Index include/filter", ddl.Contains("INCLUDE ([Price]) WHERE [Quantity] > 0"));
Check("Unique constraint", ddl.Contains("UNIQUE NONCLUSTERED ([Name])"));
Check("Descriptions escaped", ddl.Contains("包含 '' 引号"));
var parser = new TSql160Parser(true);
parser.Parse(new StringReader(ddl), out var parseErrors);
Check("Generated comprehensive DDL parses with Microsoft ScriptDom", parseErrors.Count == 0);
parser.Parse(new StringReader(customerSql), out parseErrors);
Check("Imported customer DDL parses", parseErrors.Count == 0);

TableDesign Changed(Action<TableDesign> action) { var copy = ModelJson.Clone(child); action(copy); return copy; }
void Invalid(string name, Action<TableDesign> mutate) => Check(name, SqlServerDdl.Validate(project, Changed(mutate)).Count > 0);
Invalid("Duplicate fields rejected", t => t.Columns[1].Name = "id");
Invalid("Invalid length rejected", t => t.Columns.Last().Length = "5000");
Invalid("Invalid decimal precision rejected", t => t.Columns[3].Scale = 39);
Invalid("Duplicate identity rejected", t => t.Columns[1].Identity = true);
Invalid("Nullable primary key rejected", t => t.Columns[0].Nullable = true);
Invalid("Identity default rejected", t => t.Columns[0].Default = "0");
Invalid("Missing FK target rejected", t => t.ForeignKeys[0].TargetTableId = "missing");
Invalid("FK arity mismatch rejected", t => t.ForeignKeys[0].Columns = "TenantId");
Invalid("FK target unique key order checked", t => t.ForeignKeys[0].TargetColumns = "Id,TenantId");
Invalid("FK mismatched types rejected", t => t.Columns[2].Type = "int");
Invalid("SET NULL requires nullable local columns", t => t.ForeignKeys[0].OnDelete = "SET NULL");
Invalid("SET DEFAULT requires defaults", t => t.ForeignKeys[0].OnDelete = "SET DEFAULT");
Invalid("Invalid referential action rejected", t => t.ForeignKeys[0].OnDelete = "DROP TABLE");
Invalid("Missing index columns rejected", t => t.Indexes[0].Columns = "Unknown");
Invalid("Unique constraint cannot have filter", t => t.Indexes[1].Filter = "[Id] > 0");
Invalid("Duplicate constraint names rejected", t => t.Checks[0].Name = t.ForeignKeys[0].Name);
Invalid("Multiple SQL statements rejected", t => t.Columns[3].Default = "0); DROP TABLE X;--");
Reject<InvalidOperationException>("Concurrent stale save rejected", () => store.SaveTable(admin!, project.Id, project.Revision - 1, child));
Reject<InvalidOperationException>("Referenced parent deletion rejected", () => store.SaveTable(admin!, project.Id, project.Revision, parent, true));
Reject<InvalidOperationException>("Inbound FK protects parent column changes", () => { var copy = ModelJson.Clone(parent); copy.Columns[1].Type = "int"; store.SaveTable(admin!, project.Id, project.Revision, copy); });
Check("Failed save is transactional", store.Projects(admin!).First(p => p.Id == project.Id).Revision == project.Revision);
var restarted = new StudioStore(env, config);
Check("Data persists across new store instances", restarted.Projects(admin!).First(p => p.Id == project.Id).Tables.Count == 2);

store.SaveUser(admin!, new("reader-test", "reader.test", "只读测试", "reader", true), "ReadOnly12345!");
var reader = store.Login("reader.test", "ReadOnly12345!")!;
Check("Unassigned reader sees no projects", store.Projects(reader).Count == 0);
store.SaveProjectMember(admin!, project.Id, "reader.test", ProjectAccess.Read);
Check("Reader sees only assigned project", store.Projects(reader).Select(p => p.Id).SequenceEqual([project.Id]));
Reject<UnauthorizedAccessException>("Reader cannot save", () => store.SaveTable(reader, project.Id, project.Revision, child));
Reject<UnauthorizedAccessException>("Reader cannot export", () => store.ExportProject(reader, project.Id));
Reject<UnauthorizedAccessException>("Reader cannot manage users", () => store.Users(reader));
Reject<UnauthorizedAccessException>("Reader cannot create project", () => store.NewProject(reader, "No", ""));
store.SaveRole(admin!, new("custom", "审核设计", Permission.Read | Permission.Export));
store.SaveUser(admin!, new("designer-test", "designer.test", "设计测试", "designer", true), "Designer12345!");
var designer = store.Login("designer.test", "Designer12345!")!;
store.SaveProjectMember(admin!, project.Id, "designer.test", ProjectAccess.Read | ProjectAccess.Design | ProjectAccess.Export);
child.Comment = "设计成员修改说明";
project = store.SaveTable(designer, project.Id, project.Revision, child);
Check("Designer can save", project.Revision == 4);
Reject<UnauthorizedAccessException>("Designer cannot manage roles", () => store.SaveRole(designer, new("no", "No", Permission.All)));
store.SaveRole(admin!, new("designer", "设计师", Permission.Read));
Reject<UnauthorizedAccessException>("Role revocation affects existing session", () => store.SaveTable(designer, project.Id, project.Revision, child));
store.SaveUser(admin!, new("reader-test", "reader.test", "只读测试", "reader", false), "");
Reject<UnauthorizedAccessException>("Disabled user's existing session rejected", () => store.Projects(reader));
Check("Disabled login rejected", store.Login("reader.test", "ReadOnly12345!") == null);
Reject<InvalidOperationException>("Admin role protected", () => store.SaveRole(admin!, new("admin", "管理员", Permission.Read)));
Reject<InvalidOperationException>("Admin cannot disable self", () => store.SaveUser(admin!, new("admin", "admin", "管理员", "admin", false), ""));
Reject<InvalidOperationException>("Weak password rejected", () => store.SaveUser(admin!, new("weak", "weak.test", "Weak", "reader", true), "123"));
store.SaveUser(admin!, new("locked", "locked.test", "锁定测试", "reader", true), "Locked12345!");
for (var i = 0; i < 5; i++)
{
    store.Login("locked.test", "wrong");
}

Check("Five login failures cause lockout", store.Login("locked.test", "Locked12345!") == null);
store.ChangePassword(admin!, "TestAdmin12345!", "ChangedAdmin12345!");
Reject<UnauthorizedAccessException>("Password change invalidates session", () => store.Projects(admin!));
var nextAdmin = store.Login("admin", "ChangedAdmin12345!")!;
Check("New password works", nextAdmin != null);
Check("Audit trail recorded", store.Audit(nextAdmin!).Any(a => a.Action == "修改密码"));

// 复制结构必须隔离 ID、重建约束名称，并正确区分自引用和外部引用。
var copyProject = ModelJson.Clone(project);
var sourceCopyTable = ModelJson.Clone(child);
sourceCopyTable.ForeignKeys.Add(new ForeignKeyDesign
{
    Name = "FK_Self",
    Columns = sourceCopyTable.Columns[0].Name,
    TargetColumns = sourceCopyTable.Columns[0].Name,
    TargetTableId = sourceCopyTable.Id
});
var sourceBeforeCopy = JsonSerializer.Serialize(sourceCopyTable, ModelJson.Options);
var draftCopy = DesignEditing.CopyTable(copyProject, sourceCopyTable);
Check("Copy uses independent table and column IDs", draftCopy.Id != sourceCopyTable.Id
    && !draftCopy.Columns.Select(c => c.Id).Intersect(sourceCopyTable.Columns.Select(c => c.Id)).Any());
Check("Copy preserves indexes checks defaults and computed expressions", draftCopy.Indexes.Count == sourceCopyTable.Indexes.Count
    && draftCopy.Checks.Count == sourceCopyTable.Checks.Count
    && draftCopy.Columns.Select(c => (c.Type, c.Default, c.Computed, c.Identity)).SequenceEqual(sourceCopyTable.Columns.Select(c => (c.Type, c.Default, c.Computed, c.Identity))));
Check("Copy remaps self reference and preserves external FK", draftCopy.ForeignKeys.Last().TargetTableId == draftCopy.Id
    && draftCopy.ForeignKeys.First().TargetTableId == sourceCopyTable.ForeignKeys.First().TargetTableId);
Check("Copy does not mutate source", sourceBeforeCopy == JsonSerializer.Serialize(sourceCopyTable, ModelJson.Options));
copyProject.Tables.Add(draftCopy);
Check("Repeated copy resolves table name collision", DesignEditing.CopyTable(copyProject, sourceCopyTable).Name != draftCopy.Name);
var longNameTable = ModelJson.Clone(sourceCopyTable);
longNameTable.Name = new string('A', 128);
var longNameCopy = DesignEditing.CopyTable(copyProject, longNameTable);
Check("Copy constraint names stay within identifier limit", longNameCopy.Name.Length <= 128
    && longNameCopy.Indexes.All(i => i.Name.Length <= 128)
    && longNameCopy.ForeignKeys.All(f => f.Name.Length <= 128));
var customerCopy = DesignEditing.CopyTable(imported, customer);
var customerCopyProject = ModelJson.Clone(imported);
customerCopyProject.Tables.Add(customerCopy);
Check("Copied imported table passes shared preflight", SqlServerDdl.ValidateChange(customerCopyProject, customerCopy).Count == 0);
var changedParentProject = ModelJson.Clone(project);
var changedParent = changedParentProject.Tables.First(t => t.Id == parent.Id);
changedParent.Columns.First(c => c.Name == "Id").Type = "int";
Check("Preflight detects inbound FK mismatch", SqlServerDdl.ValidateChange(changedParentProject, changedParent).Any(e => e.Contains(child.Name + " / ")));

// 备份恢复验证：预览不写库、提交重校验、引用重映射及失败原子性。
var backupJson = store.ExportProject(nextAdmin!, project.Id);
var projectCountBeforeImport = store.Projects(nextAdmin!).Count;
var backupPreview = store.PreviewImport(nextAdmin!, backupJson);
Check("Backup preview validates without writing", backupPreview.Errors.Count == 0
    && store.Projects(nextAdmin!).Count == projectCountBeforeImport);
var restored = store.ImportProject(nextAdmin!, backupJson, "恢复项目");
Check("Backup import creates independent revision one project", restored.Id != project.Id && restored.Revision == 1
    && store.Projects(nextAdmin!).Count == projectCountBeforeImport + 1);
Check("Backup import regenerates table and field IDs", !restored.Tables.Select(t => t.Id).Intersect(project.Tables.Select(t => t.Id)).Any()
    && !restored.Tables.SelectMany(t => t.Columns).Select(c => c.Id).Intersect(project.Tables.SelectMany(t => t.Columns).Select(c => c.Id)).Any());
Check("Backup import remaps external FK targets", restored.Tables.SelectMany(t => t.ForeignKeys)
    .All(f => restored.Tables.Any(t => t.Id == f.TargetTableId)));
Check("Backup import preserves source and records audit", store.ExportProject(nextAdmin!, project.Id) == backupJson
    && store.Audit(nextAdmin!).Any(a => a.Action == "导入项目备份"));
var repeatedImport = store.ImportProject(nextAdmin!, backupJson, "再次恢复");
Check("Repeated import cannot overwrite prior restore", repeatedImport.Id != restored.Id
    && !repeatedImport.Tables.Select(t => t.Id).Intersect(restored.Tables.Select(t => t.Id)).Any());
var selfProject = new DesignProject { Name = "自引用备份", Tables = [sourceCopyTable] };
// 保留自引用，移除该独立夹具没有包含的外部父表引用。
selfProject.Tables[0] = ModelJson.Clone(sourceCopyTable);
selfProject.Tables[0].ForeignKeys.RemoveAll(f => f.TargetTableId != selfProject.Tables[0].Id);
var selfImport = store.ImportProject(nextAdmin!, JsonSerializer.Serialize(selfProject, ModelJson.Options), "自引用恢复");
Check("Backup import remaps self reference", selfImport.Tables[0].ForeignKeys.All(f => f.TargetTableId == selfImport.Tables[0].Id));
Reject<InvalidOperationException>("Backup rejects malformed JSON", () => ProjectBackup.Inspect("{"));
Reject<InvalidOperationException>("Backup rejects unrelated JSON object", () => ProjectBackup.Inspect("{}"));
Reject<InvalidOperationException>("Backup rejects null model members", () => ProjectBackup.Inspect("{\"Name\":\"坏备份\",\"Dialect\":\"SqlServer\",\"Tables\":null}"));
Reject<InvalidOperationException>("Backup rejects duplicate JSON keys", () => ProjectBackup.Inspect("{\"Name\":\"A\",\"name\":\"B\",\"Dialect\":\"SqlServer\",\"Tables\":[]}"));
Reject<InvalidOperationException>("Backup rejects unsupported properties", () => ProjectBackup.Inspect("{\"Name\":\"A\",\"Dialect\":\"SqlServer\",\"Tables\":[],\"Unexpected\":1}"));
Reject<InvalidOperationException>("Backup rejects oversized input", () => ProjectBackup.Inspect(new string(' ', ProjectBackup.MaxBytes + 1)));
var duplicateIds = ModelJson.Clone(project);
duplicateIds.Tables[1].Id = duplicateIds.Tables[0].Id;
Check("Backup reports duplicate table IDs", ProjectBackup.Inspect(JsonSerializer.Serialize(duplicateIds, ModelJson.Options)).Errors.Any(e => e.Contains("表 ID")));
var missingReference = ModelJson.Clone(project);
missingReference.Tables.SelectMany(t => t.ForeignKeys).First().TargetTableId = "missing";
var brokenBackup = JsonSerializer.Serialize(missingReference, ModelJson.Options);
Check("Backup reports missing FK target", ProjectBackup.Inspect(brokenBackup).Errors.Count > 0);
var countBeforeFailure = store.Projects(nextAdmin!).Count;
Reject<InvalidOperationException>("Import rechecks invalid backup at commit", () => store.ImportProject(nextAdmin!, brokenBackup, "失败项目"));
Check("Failed import leaves project count unchanged", store.Projects(nextAdmin!).Count == countBeforeFailure);
Reject<UnauthorizedAccessException>("Reader cannot preview backup", () => store.PreviewImport(reader, backupJson));
store.SaveRole(nextAdmin!, new Role("creator-only", "仅建项目", Permission.Read | Permission.Projects));
store.SaveUser(nextAdmin!, new UserInfo("creator-only", "creator.only", "项目创建者", "creator-only", true), "CreatorOnly123!");
var creatorOnly = store.Login("creator.only", "CreatorOnly123!")!;
Reject<UnauthorizedAccessException>("Import also requires design permission", () => store.ImportProject(creatorOnly, backupJson, "越权项目"));
Check("Full source backup preview passes", ProjectBackup.Inspect(JsonSerializer.Serialize(imported, ModelJson.Options)).Errors.Count == 0);

// 项目隔离、管理和匿名分享必须在服务入口验证，不依赖界面按钮是否可见。
store.SaveRole(nextAdmin!, new Role("project-worker", "项目维护", Permission.Read | Permission.Design | Permission.Export | Permission.Projects));
store.SaveUser(nextAdmin!, new UserInfo("worker-a", "worker.a", "甲项目成员", "project-worker", true), "WorkerAlpha123!");
store.SaveUser(nextAdmin!, new UserInfo("worker-b", "worker.b", "乙项目成员", "project-worker", true), "WorkerBravo123!");
var workerA = store.Login("worker.a", "WorkerAlpha123!")!;
var workerB = store.Login("worker.b", "WorkerBravo123!")!;
var projectA = store.NewProject(workerA, "甲项目", "隔离测试");
var projectB = store.NewProject(workerB, "乙项目", "隔离测试");
Check("Creator is project manager", store.AccessTo(workerA, projectA.Id) == ProjectAccess.All);
Check("Project lists isolated", store.Projects(workerA).Select(p => p.Id).SequenceEqual([projectA.Id]) && store.Projects(workerB).Select(p => p.Id).SequenceEqual([projectB.Id]));
Reject<UnauthorizedAccessException>("Cross-project save rejected", () => store.SaveTable(workerA, projectB.Id, projectB.Revision, parent));
Reject<UnauthorizedAccessException>("Cross-project export rejected", () => store.ExportProject(workerA, projectB.Id));
Reject<UnauthorizedAccessException>("Cross-project DDL rejected", () => store.Ddl(workerA, projectB, parent));
Reject<UnauthorizedAccessException>("Cross-project rename rejected", () => store.UpdateProject(workerA, projectB.Id, projectB.Revision, "越权", ""));
Reject<UnauthorizedAccessException>("Cross-project members rejected", () => store.ProjectMembers(workerA, projectB.Id));
Reject<UnauthorizedAccessException>("Cross-project audit rejected", () => store.Audit(workerA, projectB.Id));
Reject<UnauthorizedAccessException>("Global audit rejected for ordinary member", () => store.Audit(workerA));
projectA = store.UpdateProject(workerA, projectA.Id, projectA.Revision, "甲项目新版", "修改说明");
Check("Project details persist", store.Projects(workerA).Single().Name == "甲项目新版" && store.Projects(workerA).Single().Description == "修改说明");
Reject<InvalidOperationException>("Project details stale revision rejected", () => store.UpdateProject(workerA, projectA.Id, projectA.Revision - 1, "旧版本", ""));
projectA = store.SaveModule(workerA, projectA.Id, projectA.Revision, null, "采购");
Check("Empty module persists", store.Projects(workerA).Single().Modules.SequenceEqual(["采购"]));
var moduleTable = ModelJson.Clone(parent);
moduleTable.Module = "采购";
projectA = store.SaveTable(workerA, projectA.Id, projectA.Revision, moduleTable);
projectA = store.SaveModule(workerA, projectA.Id, projectA.Revision, "采购", "供应链");
Check("Module rename updates all table membership", projectA.Modules.SequenceEqual(["供应链"]) && projectA.Tables.Single().Module == "供应链");
Reject<InvalidOperationException>("Duplicate module rejected", () => store.SaveModule(workerA, projectA.Id, projectA.Revision, null, "供应链"));
store.SaveProjectMember(workerA, projectA.Id, "worker.b", ProjectAccess.Read);
Check("Explicit membership grants only chosen project", store.Projects(workerB).Count == 2);
Reject<UnauthorizedAccessException>("Read membership cannot inherit global design access", () => store.SaveTable(workerB, projectA.Id, projectA.Revision, moduleTable));
Reject<UnauthorizedAccessException>("Read membership cannot manage members", () => store.SaveProjectMember(workerB, projectA.Id, "worker.b", ProjectAccess.All));
Reject<UnauthorizedAccessException>("Read membership cannot create share", () => store.CreateShare(workerB, projectA.Id, "越权", 1));
Check("Project audit excludes other projects", store.Audit(workerB, projectA.Id).All(a => !a.Detail.Contains("乙项目")));
store.SaveProjectMember(workerA, projectA.Id, "worker.b", ProjectAccess.None);
Reject<UnauthorizedAccessException>("Membership revocation affects active session", () => store.RequireProject(workerB, projectA.Id, ProjectAccess.Read));
Reject<InvalidOperationException>("Manager cannot remove own management access", () => store.SaveProjectMember(workerA, projectA.Id, "worker.a", ProjectAccess.Read));
var shareToken = store.CreateShare(workerA, projectA.Id, "评审", 30);
Check("Anonymous share exposes only chosen saved project", store.SharedProject(shareToken)?.Id == projectA.Id && store.SharedProject(shareToken)?.Tables.Count == 1);
Check("Invalid share token reveals nothing", store.SharedProject("bad") == null && store.SharedProject(new string('a', 64)) == null);
projectA = store.UpdateProject(workerA, projectA.Id, projectA.Revision, "实时分享新版", "");
Check("Share reflects latest saved revision", store.SharedProject(shareToken)?.Revision == projectA.Revision);
Reject<UnauthorizedAccessException>("Share token is not an editor login", () => store.SaveTable(new ClaimsPrincipal(new ClaimsIdentity([new Claim("share", shareToken)])), projectA.Id, projectA.Revision, moduleTable));
Reject<UnauthorizedAccessException>("Cannot revoke another project's share", () => store.RevokeShare(workerB, projectA.Id, store.Shares(workerA, projectA.Id).Single().Id));
store.RevokeShare(workerA, projectA.Id, store.Shares(workerA, projectA.Id).Single().Id);
Check("Revoked share immediately rejected", store.SharedProject(shareToken) == null);
var expiringToken = store.CreateShare(workerA, projectA.Id, "过期检查", 1);
using (var expiryDb = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(testPath, "studio.db")))
{
    expiryDb.Open();
    using var expiryCmd = expiryDb.CreateCommand();
    expiryCmd.CommandText = "UPDATE ProjectShares SET ExpiresAt='2000-01-01T00:00:00.0000000+00:00' WHERE Revoked=0";
    expiryCmd.ExecuteNonQuery();
}
Check("Expired share rejected", store.SharedProject(expiringToken) == null);
var cleanPath = Path.Combine(Path.GetTempPath(), "DbStudioClean-" + Guid.NewGuid().ToString("N"));
var cleanStore = new StudioStore(env, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:DataDirectory"] = cleanPath, ["Studio:InitialPassword"] = "CleanAdmin123!" }).Build());
Check("New installation has no Excel seed dependency", cleanStore.Projects(cleanStore.Login("admin", "CleanAdmin123!")!).Single().Tables.Count == 0);
ConnectionChecks.Run(store, workerA, workerB, projectA, projectB, testPath, Check);
await SharePageChecks.RunAsync(Check);
RevisionChecks.Run(store, nextAdmin!, testPath, Check);

var sourceProblems = imported.Tables.Select(t => new { t.Name, Errors = SqlServerDdl.Validate(imported, t) }).Where(x => x.Errors.Count > 0).ToList();
Check("All imported tables pass supported structural validation", sourceProblems.Count == 0);
var syntaxProblems = new List<string>();
foreach (var t in imported.Tables)
{
    parser.Parse(new StringReader(SqlServerDdl.Generate(imported, t)), out var syntaxErrors);
    if (syntaxErrors.Count > 0)
    {
        syntaxProblems.Add(t.Name + ": " + string.Join("; ", syntaxErrors.Select(e => e.Message)));
    }
}
Check("All imported table scripts parse with Microsoft ScriptDom: " + string.Join(" | ", syntaxProblems), syntaxProblems.Count == 0);
ModelPackageChecks.Run(Check);
Check("Full imported project builds as a DacFx comparison package", SqlServerTools.BuildPackage(imported).Length > 0);
File.WriteAllText(Path.Combine(root, "Seed", "validation-report.json"), JsonSerializer.Serialize(sourceProblems, ModelJson.Options));
Console.WriteLine($"SOURCE REVIEW: {sourceProblems.Count} / {imported.Tables.Count} tables have definitions needing review; see Seed/validation-report.json");
var sqlTestServer = Environment.GetEnvironmentVariable("STUDIO_SQLTEST_SERVER");
if (!string.IsNullOrWhiteSpace(sqlTestServer))
{
    await DatabaseChecks.RunAsync(store, nextAdmin!, sqlTestServer, Check);
}
Console.WriteLine($"SUCCESS: {passed} checks passed. Isolated data: {testPath}");

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "DbStudio";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
