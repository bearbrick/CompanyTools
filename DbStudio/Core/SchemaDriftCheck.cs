using System.Xml.Linq;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace DbStudio.Core;

/// <summary>快照不同后进行只读语义复核。单表核验当前表和直接外键依赖，不把整库字节变化等同于本表漂移。</summary>
public static class SchemaDriftCheck
{
    /// <summary>返回实际发生的相关结构变化；不生成可执行同步计划，不访问数据库。</summary>
    public static List<SchemaChange> FindChanges(byte[] before, byte[] current, string database,
        DesignProject project, TableDeploymentScope? scope, CancellationToken cancellationToken = default)
    {
        using var beforeStream = new MemoryStream(before);
        using var oldPackage = DacPackage.Load(beforeStream);
        using var currentStream = new MemoryStream(current);
        using var newPackage = DacPackage.Load(currentStream);
        var options = SchemaDeploymentOptions.Create(prune: true, allowDataLoss: true);
        // 这里只读比较两个快照，必须检测说明删除；绝不将此选项用于实际部署。
        options.DropExtendedPropertiesNotInSource = true;
        var result = DacServices.Script(newPackage, oldPackage, database, new PublishOptions
        {
            DeployOptions = options, GenerateDeploymentReport = true, GenerateDeploymentScript = false,
            CancelToken = cancellationToken
        });
        var changes = SchemaDeploymentBoundary.ReadChanges(XDocument.Parse(result.DeploymentReport));
        if (scope == null || changes.Count == 0) { return changes; }
        var relevant = new HashSet<SchemaChange>();
        foreach (var dependency in RelatedTables(before, current, project, scope))
        {
            relevant.UnionWith(TableDeployment.ChangesWithinScope(before, current, dependency, changes));
        }
        return relevant.ToList();
    }

    /// <summary>覆盖已有入站/出站外键，以及尚未落库设计中的外键目标。</summary>
    private static IEnumerable<TableDeploymentScope> RelatedTables(byte[] before, byte[] current, DesignProject project, TableDeploymentScope scope)
    {
        var tables = new Dictionary<string, TableDeploymentScope>(StringComparer.OrdinalIgnoreCase);
        void Add(string schema, string name) => tables[SqlServerDdl.Q(schema) + "." + SqlServerDdl.Q(name)] = new("", schema, name);
        bool Selected(TSqlObject table) => table.Name.Parts.Count == 2
            && table.Name.Parts[0].Equals(scope.Schema, StringComparison.OrdinalIgnoreCase)
            && table.Name.Parts[1].Equals(scope.Name, StringComparison.OrdinalIgnoreCase);
        Add(scope.Schema, scope.Name);
        var design = project.Tables.Single(t => t.Id == scope.TableId);
        foreach (var key in design.ForeignKeys)
        {
            var target = project.Tables.Single(t => t.Id == key.TargetTableId);
            Add(target.Schema, target.Name);
        }
        foreach (var bytes in new[] { before, current })
        {
            using var input = new MemoryStream(bytes);
            using var model = TSqlModel.LoadFromDacpac(input, new ModelLoadOptions());
            foreach (var key in model.GetObjects(DacQueryScopes.UserDefined, ForeignKeyConstraint.TypeClass))
            {
                var hosts = key.GetReferenced(ForeignKeyConstraint.Host).Concat(key.GetReferenced(ForeignKeyConstraint.ForeignTable)).ToList();
                if (!hosts.Any(Selected)) { continue; }
                foreach (var host in hosts.Where(t => t.Name.Parts.Count == 2)) { Add(host.Name.Parts[0], host.Name.Parts[1]); }
            }
        }
        return tables.Values;
    }
}
