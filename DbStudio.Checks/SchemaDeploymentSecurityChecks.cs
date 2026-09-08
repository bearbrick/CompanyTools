using System.Text.RegularExpressions;
using DbStudio.Core;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

/// <summary>模拟提取包丢失登录映射，验证真实目标比对不会重建用户；仅在内存生成脚本。</summary>
internal static class SchemaDeploymentSecurityChecks
{
    internal static void Run(Action<string, bool> check)
    {
        const string table = "CREATE TABLE dbo.SecurityScoped (Id int NOT NULL PRIMARY KEY, Monthly int NULL);\nGO\n";
        const string index = "CREATE INDEX IX_SecurityScoped_Monthly ON dbo.SecurityScoped(Monthly);\nGO\n";
        using var target = Package(table + """
            CREATE LOGIN [pq] WITH PASSWORD = 'OfflineModelOnly123!';
            GO
            CREATE USER [pq] FOR LOGIN [pq];
            GO
            CREATE ROLE [ExistingReader];
            GO
            ALTER ROLE [ExistingReader] ADD MEMBER [pq];
            GO
            GRANT SELECT ON dbo.SecurityScoped TO [ExistingReader];
            GO
            """);
        // 与 IgnoreUserLoginMappings 提取结果一致：保留用户但丢失 FOR LOGIN 关系。
        using var source = Package(table + index + "CREATE USER [pq] WITHOUT LOGIN;\nGO\n");
        var previous = Script(source, new DacDeployOptions
        {
            ScriptDatabaseOptions = false, IgnorePermissions = true, IgnoreRoleMembership = true,
            DoNotDropObjectTypes = [ObjectType.Users, ObjectType.Logins, ObjectType.DatabaseRoles]
        });
        check("Security regression reproduces unwanted CREATE USER with previous options", previous.Contains("CREATE USER [pq] WITHOUT LOGIN"));

        foreach (var prune in new[] { false, true })
        {
            var script = Script(source, SchemaDeploymentOptions.Create(prune, false));
            check($"Mapped login user is excluded from deployment (prune={prune})", !HasSecurityChanges(script));
            check($"Ordinary index still deploys with security exclusion (prune={prune})", script.Contains("CREATE NONCLUSTERED INDEX [IX_SecurityScoped_Monthly]"));
            // 整库设计通常不包含任何账号；即使启用清理，也不得删除现有安全对象。
            using var designOnly = Package(table + index);
            check($"Whole-project source preserves target security (prune={prune})", !HasSecurityChanges(Script(designOnly, SchemaDeploymentOptions.Create(prune, false))));
        }

        string Script(DacPackage package, DacDeployOptions options)
            => DacServices.GenerateDeployScript(package, target, "SecurityRegression", options);
    }

    private static bool HasSecurityChanges(string script) => Regex.IsMatch(script,
        @"\b(?:CREATE|ALTER|DROP)\s+(?:USER|LOGIN|ROLE|APPLICATION\s+ROLE)\b|\b(?:GRANT|DENY|REVOKE)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static DacPackage Package(string script)
    {
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        model.AddObjects(script);
        using var output = new MemoryStream();
        DacPackageExtensions.BuildPackage(output, model, new PackageMetadata { Name = "SecurityRegression" });
        return DacPackage.Load(new MemoryStream(output.ToArray()));
    }
}
