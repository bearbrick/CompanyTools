using System.Globalization;
using System.Xml.Linq;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbStudio.Core;

/// <summary>只对明确保留容量的类型扩展取消 DacFx 的通用数据丢失阻断；混合计划仍保守处理。</summary>
public static class SchemaTypeExpansion
{
    /// <summary>返回已证明的扩展列；只有复原这些类型后整份计划不再包含其他结构变化，才可自动放行。</summary>
    public static List<string> Assess(byte[] sourceBytes, byte[] targetBytes, string database, bool prune, CancellationToken token = default, List<string>? reductions = null)
    {
        using var sourceStream = new MemoryStream(sourceBytes);
        using var source = TSqlModel.LoadFromDacpac(sourceStream, new ModelLoadOptions { LoadAsScriptBackedModel = true });
        using var targetStream = new MemoryStream(targetBytes);
        using var target = TSqlModel.LoadFromDacpac(targetStream, new ModelLoadOptions());
        var targets = target.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass).ToDictionary(t => t.Name.ToString(), StringComparer.Ordinal);
        var expanded = new List<string>();
        foreach (var table in source.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass).ToList())
        {
            token.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(table.Name.ToString(), out var oldTable)) { continue; }
            var oldDefinition = Definition(oldTable.GetScript());
            var definition = Definition(table.GetScript());
            if (oldDefinition == null || definition == null) { return []; }
            var oldColumns = oldDefinition.Definition.ColumnDefinitions.ToDictionary(c => c.ColumnIdentifier.Value, StringComparer.Ordinal);
            var changed = false;
            foreach (var column in definition.Definition.ColumnDefinitions)
            {
                if (!oldColumns.TryGetValue(column.ColumnIdentifier.Value, out var oldColumn)) { continue; }
                if (IsReduction(oldColumn.DataType, column.DataType))
                {
                    reductions?.Add($"{table.Name}.{SqlServerDdl.Q(column.ColumnIdentifier.Value)}：{Sql(oldColumn.DataType)} → {Sql(column.DataType)}");
                }
                if (!IsExpansion(oldColumn.DataType, column.DataType)) { continue; }
                expanded.Add($"{table.Name}.{SqlServerDdl.Q(column.ColumnIdentifier.Value)}：{Sql(oldColumn.DataType)} → {Sql(column.DataType)}");
                // 仅修改内存里的审查副本。实际部署仍使用原始设计包。
                column.DataType = oldColumn.DataType;
                changed = true;
            }
            if (changed) { source.AddOrUpdateObjects(Sql(definition), table.GetSourceInformation().SourceName, new TSqlObjectOptions()); }
        }
        if (expanded.Count == 0) { return []; }
        using var normalized = new MemoryStream();
        DacPackageExtensions.BuildPackage(normalized, source, new PackageMetadata { Name = "ExpansionAssessment" });
        normalized.Position = 0;
        using var normalizedPackage = DacPackage.Load(normalized);
        using var originalTargetStream = new MemoryStream(targetBytes);
        using var targetPackage = DacPackage.Load(originalTargetStream);
        var result = DacServices.Script(normalizedPackage, targetPackage, database, new PublishOptions
        {
            DeployOptions = SchemaDeploymentOptions.Create(prune, false), GenerateDeploymentReport = true,
            GenerateDeploymentScript = false, CancelToken = token
        });
        var report = XDocument.Parse(result.DeploymentReport);
        // 全局开关不能逐列关闭：删表、删列、收窄、非空/自增/约束/其他类型变化均不能跟随放行。
        var remaining = SchemaDeploymentBoundary.ReadChanges(report);
        return remaining.All(c => c.ObjectType == "SqlExtendedProperty")
            && !report.Descendants().Any(e => e.Name.LocalName == "Alert" && (string?)e.Attribute("Name") == "DataIssue") ? expanded : [];
    }

    /// <summary>仅允许可由类型范围证明的扩展。nvarchar 到 varchar、固定长度变换和未知类型不自动放行。</summary>
    public static bool IsExpansion(DataTypeReference? oldType, DataTypeReference? newType)
    {
        if (oldType is not SqlDataTypeReference a || newType is not SqlDataTypeReference b || Sql(a) == Sql(b)) { return false; }
        var variable = a.SqlDataTypeOption is SqlDataTypeOption.VarChar or SqlDataTypeOption.NVarChar or SqlDataTypeOption.VarBinary;
        if (variable && (a.SqlDataTypeOption == b.SqlDataTypeOption
            || a.SqlDataTypeOption == SqlDataTypeOption.VarChar && b.SqlDataTypeOption == SqlDataTypeOption.NVarChar))
        {
            var oldLength = Length(a); var newLength = Length(b);
            // 两种 max 的最大字节容量相同，varchar(max) 到 nvarchar(max) 不能仅凭类型保证容量。
            return oldLength > 0 && newLength >= oldLength
                && !(oldLength == long.MaxValue && a.SqlDataTypeOption != b.SqlDataTypeOption);
        }
        var integers = new[] { SqlDataTypeOption.TinyInt, SqlDataTypeOption.SmallInt, SqlDataTypeOption.Int, SqlDataTypeOption.BigInt };
        var oldRank = Array.IndexOf(integers, a.SqlDataTypeOption); var newRank = Array.IndexOf(integers, b.SqlDataTypeOption);
        if (oldRank >= 0 && newRank > oldRank) { return true; }
        if (a.SqlDataTypeOption is SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric
            && b.SqlDataTypeOption is SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric)
        {
            var (ap, ass) = Precision(a); var (bp, bs) = Precision(b);
            return bs >= ass && bp - bs >= ap - ass;
        }
        return false;
    }

    /// <summary>识别明确的容量收窄，用于逐列提示截断、舍入或溢出风险；不声称现有数据一定越界。</summary>
    public static bool IsReduction(DataTypeReference? oldType, DataTypeReference? newType)
    {
        if (oldType is not SqlDataTypeReference a || newType is not SqlDataTypeReference b) { return false; }
        if ((a.SqlDataTypeOption == b.SqlDataTypeOption && a.SqlDataTypeOption is
            SqlDataTypeOption.VarChar or SqlDataTypeOption.NVarChar or SqlDataTypeOption.VarBinary or
            SqlDataTypeOption.Char or SqlDataTypeOption.NChar or SqlDataTypeOption.Binary)
            || a.SqlDataTypeOption == SqlDataTypeOption.VarChar && b.SqlDataTypeOption == SqlDataTypeOption.NVarChar)
        {
            return Length(b) < Length(a);
        }
        if (a.SqlDataTypeOption is SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric
            && b.SqlDataTypeOption is SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric)
        {
            var (ap, ass) = Precision(a); var (bp, bs) = Precision(b);
            return bs < ass || bp - bs < ap - ass;
        }
        var integers = new[] { SqlDataTypeOption.TinyInt, SqlDataTypeOption.SmallInt, SqlDataTypeOption.Int, SqlDataTypeOption.BigInt };
        var newRank = Array.IndexOf(integers, b.SqlDataTypeOption);
        return newRank >= 0 && newRank < Array.IndexOf(integers, a.SqlDataTypeOption);
    }

    private static long Length(SqlDataTypeReference type) => type.Parameters.Count == 1
        ? type.Parameters[0].Value.Equals("max", StringComparison.OrdinalIgnoreCase) ? long.MaxValue
            : long.Parse(type.Parameters[0].Value, CultureInfo.InvariantCulture) : 1;
    private static (int Precision, int Scale) Precision(SqlDataTypeReference type) =>
        (type.Parameters.Count > 0 ? int.Parse(type.Parameters[0].Value, CultureInfo.InvariantCulture) : 18,
         type.Parameters.Count > 1 ? int.Parse(type.Parameters[1].Value, CultureInfo.InvariantCulture) : 0);
    private static string Sql(TSqlFragment fragment) { new Sql160ScriptGenerator().GenerateScript(fragment, out var text); return text; }
    private static CreateTableStatement? Definition(string sql)
    {
        var fragment = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
        return errors.Count == 0 && fragment is TSqlScript script ? script.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().SingleOrDefault() : null;
    }
}
