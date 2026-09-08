using System.Xml.Linq;
using Microsoft.SqlServer.Dac.Model;

namespace DbStudio.Core;

/// <summary>整库和单表共用的写入边界。遇到未知或越界对象时拒绝整个计划，不静默过滤报告。</summary>
public static class SchemaDeploymentBoundary
{
    private static readonly HashSet<string> ManagedTypes = new(StringComparer.Ordinal)
    {
        "SqlTable", "SqlSimpleColumn", "SqlComputedColumn", "SqlColumn", "SqlIndex",
        "SqlPrimaryKeyConstraint", "SqlUniqueConstraint", "SqlForeignKeyConstraint",
        "SqlDefaultConstraint", "SqlCheckConstraint", "SqlExtendedProperty", "SqlSchema"
    };

    /// <summary>统一解析 DacFx 部署报告，预览和实际执行前复核使用相同规则。</summary>
    public static List<SchemaChange> ReadChanges(XDocument report) => report.Descendants()
        .Where(e => e.Name.LocalName == "Operation")
        .SelectMany(operation => operation.Descendants().Where(e => e.Name.LocalName == "Item")
            .Select(item => new SchemaChange((string?)operation.Attribute("Name") ?? "",
                (string?)item.Attribute("Type") ?? "", (string?)item.Attribute("Value") ?? ""))).ToList();

    /// <summary>仅放行表及其设计对象，扩展属性必须属于表或字段，schema 仅允许为表创建。</summary>
    public static void Validate(byte[] source, byte[] target, TableDeploymentScope? scope, IReadOnlyList<SchemaChange> changes)
    {
        var outside = changes.Where(change => !ManagedTypes.Contains(change.ObjectType)
            || change.Operation is not ("Create" or "Alter" or "Drop")
            || change.ObjectType == "SqlSchema" && change.Operation != "Create").ToList();
        Reject(outside);
        if (scope != null)
        {
            TableDeployment.ValidateChanges(source, target, scope, changes);
        }

        if (!changes.Any(change => change.ObjectType is "SqlExtendedProperty" or "SqlSchema")) { return; }
        var tableProperties = new HashSet<string>(StringComparer.Ordinal);
        var tableSchemas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bytes in new[] { source, target })
        {
            using var stream = new MemoryStream(bytes);
            using var model = TSqlModel.LoadFromDacpac(stream, new ModelLoadOptions());
            foreach (var table in model.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass))
            {
                tableSchemas.Add(SqlServerDdl.Q(table.Name.Parts[0]));
            }
            foreach (var property in model.GetObjects(DacQueryScopes.UserDefined, ExtendedProperty.TypeClass)
                .Where(property => property.GetReferenced(ExtendedProperty.Host)
                    .Any(host => host.ObjectType.Name == "Table"
                        || host.ObjectType.Name == "Column" && host.GetParent()?.ObjectType.Name == "Table")))
            {
                tableProperties.Add(property.Name.ToString());
                tableProperties.Add(model.DisplayServices.GetElementName(property, ElementNameStyle.EscapedFullyQualifiedName));
                tableProperties.Add(string.Join(".", property.Name.Parts.Skip(1).Select(SqlServerDdl.Q)));
            }
        }
        Reject(changes.Where(change => change.ObjectType == "SqlExtendedProperty" && !tableProperties.Contains(change.Name)
            || change.ObjectType == "SqlSchema" && !tableSchemas.Contains(change.Name)).ToList());
    }

    /// <summary>实际连接重新规划若与用户预览不同，必须重新核对，不执行后来追加的操作。</summary>
    public static void ValidateApproved(IReadOnlyList<SchemaChange> approved, IReadOnlyList<SchemaChange> actual)
    {
        if (!approved.ToHashSet().SetEquals(actual))
        {
            throw new InvalidOperationException("实际部署计划与已预览的差异不一致，同步已停止，请重新比对并核对。");
        }
    }

    private static void Reject(IReadOnlyList<SchemaChange> changes)
    {
        if (changes.Count == 0) { return; }
        throw new InvalidOperationException("同步计划包含表结构范围之外的操作，已停止执行：\n"
            + string.Join("\n", changes.Take(8).Select(change => $"{change.Operation} · {change.ObjectType} · {change.Name}")));
    }
}
