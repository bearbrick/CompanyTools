using System.Security.Claims;
using DbStudio.Core;
using PdfSharp.Pdf.IO;

/// <summary>归档真实服务授权、结构语义及大文档分页检查；样例文件写入隔离测试目录供视觉复查。</summary>
internal static class StructureArchiveChecks
{
    internal static async Task RunAsync(StudioStore store, ClaimsPrincipal admin, ClaimsPrincipal outsider, string outputPath, Action<string, bool> check)
    {
        var project = store.NewProject(admin, "数据库归档检查", "用于核验正式首页、紧凑表结构与正式审批尾页。");
        var table = new TableDesign
        {
            Name = "Employee",
            Label = "员工档案",
            Module = "人事",
            Comment = "记录员工信息；中文备注、英文标识符与空值需要完整保留。",
            PrimaryKeyName = "PK_Employee_Custom",
            PrimaryKeyClustered = false,
            PrimaryKeyDescendingColumns = "Id",
            Columns =
            [
                new() { Name = "Id", Label = "员工标识", Type = "bigint", Nullable = false, PrimaryKeyOrder = 1, Identity = true, IdentitySeed = 10, IdentityIncrement = 2 },
                new() { Name = "Code", Label = "员工编号", Type = "nvarchar", Length = "80", Nullable = false, Collation = "Latin1_General_100_CI_AS", InputLimit = "20" },
                new() { Name = "Remark", Label = "备注", Type = "nvarchar", Length = "max", Default = "NULL", DefaultConstraintName = "DF_Employee_Remark", Comment = "选填信息；未录入时为空。" },
                new() { Name = "Amount", Label = "金额", Type = "decimal", Precision = 19, Scale = 4, Default = "0" },
                new() { Name = "CreatedAt", Label = "创建时间", Type = "datetime2", TemporalScale = 3, Nullable = false, Default = "sysdatetime()" },
                new() { Name = "ComputedId", Label = "派生标识", Computed = "[Id] * 2", Persisted = true }
            ],
            Indexes = [new() { Name = "IX_Employee_Code", Columns = "Code,Id", DescendingColumns = "Code", Include = "Amount", Filter = "[Amount] > 0", Unique = true }],
            Checks = [new() { Name = "CK_Employee_Amount", Expression = "[Amount] >= 0" }]
        };
        project = store.SaveTable(admin, project.Id, project.Revision, table);
        var options = new StructureArchiveOptions { DocumentNumber = "DB-ARCH-TEST-001", PreparedBy = "测试编制人", Department = "信息技术部", DatabaseName = "ArchiveTest", DatabaseVersion = "2022", Environment = "验证环境" };
        var snapshot = store.ReadStructureArchive(admin, project.Id, project.Revision, options);
        table.Columns[1].Comment = "尚未保存的内容";
        options.PreparedBy = "后续修改";
        check("Archive uses isolated saved snapshot and copied metadata", snapshot.Project.Tables[0].Columns[1].Comment == "" && snapshot.Options.PreparedBy == "测试编制人");
        var denied = false;
        try
        {
            store.ReadStructureArchive(outsider, project.Id, project.Revision, options);
        }
        catch (UnauthorizedAccessException) { denied = true; }
        check("Archive rejects cross-project access", denied);
        store.SaveUser(admin, new("archive-reader", "archive.reader", "归档只读检查", "reader", true), "ArchiveReader123!");
        store.SaveProjectMember(admin, project.Id, "archive.reader", ProjectAccess.Read | ProjectAccess.Export);
        var reader = store.Login("archive.reader", "ArchiveReader123!")!;
        var globalDenied = false;
        try
        {
            store.ReadStructureArchive(reader, project.Id, project.Revision, options);
        }
        catch (UnauthorizedAccessException)
        {
            globalDenied = true;
        }
        check("Archive also enforces global export permission", globalDenied);
        var outsiderLogin = store.Require(outsider, Permission.Read).Login;
        store.SaveProjectMember(admin, project.Id, outsiderLogin, ProjectAccess.Read);
        var projectDenied = false;
        try
        {
            store.ReadStructureArchive(outsider, project.Id, project.Revision, options);
        }
        catch (UnauthorizedAccessException)
        {
            projectDenied = true;
        }
        check("Archive read membership cannot inherit global export permission", projectDenied);
        var stale = false;
        try
        {
            store.ReadStructureArchive(admin, project.Id, project.Revision - 1, options);
        }
        catch (InvalidOperationException) { stale = true; }
        check("Archive rejects stale project revision", stale);
        var empty = store.NewProject(admin, "空白归档检查", "");
        var emptyRejected = false;
        try
        {
            store.ReadStructureArchive(admin, empty.Id, empty.Revision, options);
        }
        catch (InvalidOperationException) { emptyRejected = true; }
        check("Archive rejects empty project", emptyRejected);
        var invalidMetadata = false;
        try
        {
            store.ReadStructureArchive(admin, project.Id, project.Revision, new()
            {
                Version = "\r\nInjected"
            });
        }
        catch (InvalidOperationException) { invalidMetadata = true; }
        check("Archive validates metadata on server", invalidMetadata);
        check("Nullable cells are blank and computed fields are not guessed", StructureArchiveText.Nullability(table.Columns[2]) == "" && StructureArchiveText.Nullability(table.Columns[0]) == "不可空" && StructureArchiveText.DataType(table.Columns[5]) == "计算列");
        check("Archive preserves type parameters and default distinction", StructureArchiveText.DataType(table.Columns[3]) == "decimal(19,4)" && StructureArchiveText.DataType(table.Columns[4]) == "datetime2(3)" && StructureArchiveText.Generation(table.Columns[2]) == "NULL" && StructureArchiveText.Generation(table.Columns[1]) == "无");
        check("Archive preserves identity and persisted expression", StructureArchiveText.Generation(table.Columns[0]).Contains("起始 10，步长 2") && StructureArchiveText.Generation(table.Columns[5]).Contains("持久化计算：[Id] * 2"));
        var notes = string.Join('\n', StructureArchiveText.Notes(project, table));
        check("Archive preserves named keys, direction, includes and filter", notes.Contains("PK_Employee_Custom") && notes.Contains("Id 降序") && notes.Contains("Code 降序，Id 升序") && notes.Contains("包含列：Amount") && notes.Contains("筛选条件：[Amount] > 0") && notes.Contains("Latin1_General_100_CI_AS") && notes.Contains("DF_Employee_Remark"));
        var related = ModelJson.Clone(table);
        related.ForeignKeys.Add(new()
        {
            Name = "FK_Self",
            Columns = "Id",
            TargetTableId = table.Id,
            TargetColumns = "Id",
            OnDelete = "CASCADE",
            OnUpdate = "SET DEFAULT"
        });
        notes = string.Join('\n', StructureArchiveText.Notes(project, related));
        check("Archive resolves FK targets and describes both actions", notes.Contains("dbo.Employee (Id)") && notes.Contains("级联（CASCADE）") && notes.Contains("设为默认值（SET DEFAULT）"));

        var pdf = new StructureArchivePdf();
        var auditBefore = store.Audit(admin, project.Id).Count;
        var sample = await pdf.GenerateAsync(snapshot);
        var samplePath = Path.Combine(outputPath, "structure-archive-sample.pdf");
        File.WriteAllBytes(samplePath, sample);
        using (var doc = PdfReader.Open(new MemoryStream(sample), PdfDocumentOpenMode.Import))
        {
            check("Small archive contains separate cover, compact body and approval pages", doc.PageCount == 3 && doc.Pages.Cast<PdfSharp.Pdf.PdfPage>().All(p => Math.Abs(p.Width.Point - 595) < 1));
        }
        check("Archive does not write project or audit", store.Projects(admin).Single(p => p.Id == project.Id).Revision == project.Revision && store.Audit(admin, project.Id).Count == auditBefore);

        var large = new DesignProject { Name = "一百张表结构归档验证", Revision = 8, Modules = ["基础资料", "业务记录"] };
        for (var i = 0; i < 100; i++)
        {
            var item = new TableDesign { Name = $"ArchiveTable{i:000}", Label = $"示例数据表 {i:000}", Module = i % 2 == 0 ? "基础资料" : "业务记录" };
            for (var field = 0; field < 10; field++)
            {
                item.Columns.Add(new()
                {
                    Name = $"Field{field:00}",
                    Label = $"业务字段 {field:00}",
                    Type = "nvarchar",
                    Length = "80",
                    Nullable = field != 0,
                    Comment = field == 8 ? "枚举值：1=有效；2=停用" : ""
                });
            }
            large.Tables.Add(item);
        }
        var largeSnapshot = new StructureArchive(large, snapshot.Options, snapshot.ExportedAt);
        var largeBytes = await pdf.GenerateAsync(largeSnapshot);
        using (var doc = PdfReader.Open(new MemoryStream(largeBytes), PdfDocumentOpenMode.Import))
        {
            check("100 tables remain compact with embedded Chinese text", doc.PageCount is > 20 and < 70 && largeBytes.Length < 5_000_000);
            Console.WriteLine($"ARCHIVE: 100 tables / 1000 fields = {doc.PageCount} pages, {largeBytes.Length} bytes");
        }
        File.WriteAllBytes(Path.Combine(outputPath, "structure-archive-100-tables.pdf"), largeBytes);
        check("Archive respects module order without changing project", StructureArchiveText.Tables(large).Take(50).All(t => t.Module == "基础资料") && large.Tables[1].Name == "ArchiveTable001");

        var longProject = ModelJson.Clone(project);
        longProject.Name = "长字段备注与长表续页验证";
        longProject.Tables[0].Columns[2].Comment = string.Concat(Enumerable.Repeat("长备注需完整换行；支持连续中文和_very_long_identifier_without_spaces。", 300)) + "长备注结束标识";
        for (var i = 0; i < 80; i++)
        {
            longProject.Tables[0].Columns.Add(new()
            {
                Name = $"Continuation{i:000}",
                Label = $"续页字段 {i:000}"
            });
        }
        var longBytes = await pdf.GenerateAsync(new(longProject, snapshot.Options, snapshot.ExportedAt));
        using (var doc = PdfReader.Open(new MemoryStream(longBytes), PdfDocumentOpenMode.Import))
        {
            check("Oversized field text uses full width and long tables paginate", doc.PageCount is > 3 and < 12);
        }
        File.WriteAllBytes(Path.Combine(outputPath, "structure-archive-long-text.pdf"), longBytes);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledCorrectly = false;
        try
        {
            await pdf.GenerateAsync(snapshot, cancelled.Token);
        }
        catch (OperationCanceledException) { cancelledCorrectly = true; }
        check("Archive export responds to cancellation", cancelledCorrectly);
        Console.WriteLine($"ARCHIVE QA FILES: {outputPath}");
    }
}
