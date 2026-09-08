using System.Xml.Linq;
using DbStudio.Core;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

/// <summary>验证表同步不会夹带管理对象、所有者、触发器状态或其他模块操作。</summary>
internal static class SchemaDeploymentBoundaryChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var before = Package(Script(false));
        var after = Package(Script(true));
        using var target = DacPackage.Load(new MemoryStream(before));
        using var source = DacPackage.Load(new MemoryStream(after));
        foreach (var prune in new[] { false, true })
        {
            var result = DacServices.Script(source, target, "BoundaryTest", new PublishOptions
            {
                DeployOptions = SchemaDeploymentOptions.Create(prune, false),
                GenerateDeploymentReport = true, GenerateDeploymentScript = true
            });
            var changes = SchemaDeploymentBoundary.ReadChanges(XDocument.Parse(result.DeploymentReport));
            SchemaDeploymentBoundary.Validate(after, before, null, changes);
            check($"Boundary keeps real table column changes (prune={prune})", changes.Any(c => c.ObjectType == "SqlTable") && result.DatabaseScript.Contains("[Added]"));
            check($"Boundary excludes changed views procedures functions sequences certificates (prune={prune})",
                changes.All(c => c.ObjectType is not ("SqlView" or "SqlProcedure" or "SqlScalarFunction" or "SqlSequence" or "SqlCertificate")));
            check($"Boundary never disables database triggers or changes authorization (prune={prune})",
                !result.DatabaseScript.Contains("DISABLE TRIGGER", StringComparison.OrdinalIgnoreCase)
                && !result.DatabaseScript.Contains("ENABLE TRIGGER", StringComparison.OrdinalIgnoreCase)
                && !result.DatabaseScript.Contains("ALTER AUTHORIZATION", StringComparison.OrdinalIgnoreCase)
                && !result.DatabaseScript.Contains("sp_refreshsqlmodule", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var type in new[] { "SqlUser", "SqlLogin", "SqlRole", "SqlPermission", "SqlCertificate", "SqlSymmetricKey",
            "SqlLinkedServer", "SqlDatabaseOptions", "SqlFilegroup", "SqlDatabaseDdlTrigger", "SqlDmlTrigger", "SqlView", "SqlProcedure", "SqlUnknownFutureType" })
        {
            check($"Boundary rejects unexpected {type} even if the deployment engine reports it", Reject(() =>
                SchemaDeploymentBoundary.Validate(after, before, null, [new("Alter", type, "[Unexpected]")])));
        }
        check("Boundary refuses schema ownership changes", Reject(() => SchemaDeploymentBoundary.Validate(after, before, null, [new("Alter", "SqlSchema", "[app]")])));
        check("Boundary refuses database-level extended properties", Reject(() => SchemaDeploymentBoundary.Validate(after, before, null,
            [new("Alter", "SqlExtendedProperty", "[DatabaseNote]")])));
        check("Boundary rejects unrelated new schemas", Reject(() => SchemaDeploymentBoundary.Validate(after, before, null, [new("Create", "SqlSchema", "[unrelated]")])));
        SchemaDeploymentBoundary.Validate(after, before, null, [new("Create", "SqlSchema", "[app]")]);
        check("Boundary allows schema required by a design table", true);
        var approved = new List<SchemaChange> { new("Alter", "SqlTable", "[app].[Scoped]") };
        check("Execution rejects an additional live operation", Reject(() => SchemaDeploymentBoundary.ValidateApproved(approved,
            [.. approved, new("Create", "SqlIndex", "[app].[Scoped].[Unexpected]")])));
        SchemaDeploymentBoundary.ValidateApproved(approved, approved.AsEnumerable().Reverse().ToList());
        check("Execution accepts the same reviewed operations", true);
    }

    private static string Script(bool changed) => $$"""
        CREATE USER [SchemaOwner] WITHOUT LOGIN;
        GO
        CREATE SCHEMA [app] AUTHORIZATION [{{(changed ? "dbo" : "SchemaOwner")}}];
        GO
        CREATE TABLE app.Scoped (Id int NOT NULL PRIMARY KEY{{(changed ? ", Added int NULL" : "")}});
        GO
        CREATE VIEW app.ProtectedView AS SELECT Id, {{(changed ? "2" : "1")}} AS Flag FROM app.Scoped;
        GO
        CREATE PROCEDURE app.ProtectedProc AS SELECT {{(changed ? "2" : "1")}};
        GO
        CREATE FUNCTION app.ProtectedFunction() RETURNS int AS BEGIN RETURN {{(changed ? "2" : "1")}}; END;
        GO
        CREATE SEQUENCE app.ProtectedSequence AS int START WITH 1 INCREMENT BY {{(changed ? "2" : "1")}};
        GO
        CREATE CERTIFICATE ProtectedCertificate WITH SUBJECT = '{{(changed ? "changed" : "original")}}';
        GO
        CREATE TRIGGER ProtectedDatabaseTrigger ON DATABASE FOR CREATE_TABLE AS PRINT 'preserved';
        GO
        EXEC sys.sp_addextendedproperty @name=N'DatabaseNote', @value=N'{{(changed ? "changed" : "original")}}';
        GO
        """;

    private static byte[] Package(string script)
    {
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        model.AddObjects(script);
        using var output = new MemoryStream();
        DacPackageExtensions.BuildPackage(output, model, new PackageMetadata { Name = "BoundaryRegression" });
        return output.ToArray();
    }

    private static bool Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }
}
