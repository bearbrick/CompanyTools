using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbStudio.Core;

/// <summary>一项需要明确授权的结构数据风险。</summary>
public sealed record DataLossRisk(string Code, string Title, string Detail);

/// <summary>将 DacFx 诊断与结构差异收敛为可审阅、可确认的高风险清单。</summary>
public static class DataLossAssessment
{
    private static readonly HashSet<string> ColumnTypes = new(StringComparer.Ordinal)
    {
        "SqlSimpleColumn", "SqlComputedColumn", "SqlColumn"
    };

    /// <summary>识别明确删除数据的操作、容量收窄及 DacFx 报告的数据兼容性问题。</summary>
    public static List<DataLossRisk> Assess(IReadOnlyList<SchemaChange> changes,
        IReadOnlyList<DeploymentWarning> warnings, IReadOnlyList<string> reductions)
    {
        var result = new List<DataLossRisk>();
        result.AddRange(changes.Where(change => change.Operation == "Drop" && change.ObjectType == "SqlTable")
            .Select(change => new DataLossRisk("DropTable", "删除数据表", change.Name)));
        result.AddRange(changes.Where(change => change.Operation == "Drop" && ColumnTypes.Contains(change.ObjectType))
            .Select(change => new DataLossRisk("DropColumn", "删除字段", change.Name)));
        result.AddRange(reductions.Select(detail => new DataLossRisk("TypeCapacityReduction", "缩小字段容量", detail)));
        result.AddRange(warnings.Where(warning => warning.Code == "DataIssue")
            .SelectMany(warning => warning.Issues.Select(issue => new DataLossRisk("DataIssue", "数据兼容性风险", issue.Message))));
        return result.DistinctBy(risk => (risk.Code, risk.Detail)).ToList();
    }

    /// <summary>在报告诊断之外直接比对模型，避免 DacFx 解锁后不再输出 DataIssue 而遗漏删列。</summary>
    public static List<DataLossRisk> Assess(byte[] sourceBytes, byte[] targetBytes, bool prune,
        IReadOnlyList<SchemaChange> changes, IReadOnlyList<DeploymentWarning> warnings,
        IReadOnlyList<string> reductions, CancellationToken token = default)
    {
        var result = Assess(changes, warnings, reductions);
        using var sourceStream = new MemoryStream(sourceBytes);
        using var source = TSqlModel.LoadFromDacpac(sourceStream, new ModelLoadOptions());
        using var targetStream = new MemoryStream(targetBytes);
        using var target = TSqlModel.LoadFromDacpac(targetStream, new ModelLoadOptions());
        var sourceTables = source.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass)
            .ToDictionary(table => table.Name.ToString(), StringComparer.Ordinal);
        foreach (var targetTable in target.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass))
        {
            token.ThrowIfCancellationRequested();
            if (!sourceTables.TryGetValue(targetTable.Name.ToString(), out var sourceTable))
            {
                if (prune)
                {
                    result.Add(new("DropTable", "删除数据表", targetTable.Name.ToString()));
                }
                continue;
            }
            var sourceColumns = Columns(sourceTable).ToHashSet(StringComparer.Ordinal);
            result.AddRange(Columns(targetTable).Where(column => !sourceColumns.Contains(column))
                .Select(column => new DataLossRisk("DropColumn", "删除字段", $"{targetTable.Name}.{SqlServerDdl.Q(column)}")));
        }
        return result.DistinctBy(risk => (risk.Code, risk.Detail)).ToList();
    }

    /// <summary>生成与目标库绑定的确认短语，避免复用普通发布确认。</summary>
    public static string Confirmation(string database) => $"允许数据损失 {database}";

    private static IEnumerable<string> Columns(TSqlObject table)
    {
        var fragment = new TSql160Parser(true).Parse(new StringReader(table.GetScript()), out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
        {
            throw new InvalidOperationException($"无法解析表 {table.Name} 以识别高风险字段删除。");
        }
        return script.Batches.SelectMany(batch => batch.Statements).OfType<CreateTableStatement>()
            .Single().Definition.ColumnDefinitions.Select(column => column.ColumnIdentifier.Value).ToList();
    }
}
