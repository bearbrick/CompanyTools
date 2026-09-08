using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace DbStudio.Core;

/// <summary>单表部署范围，使用稳定设计 ID 与明确的架构、物理表名绑定计划。</summary>
public sealed record TableDeploymentScope(string TableId, string Schema, string Name)
{
    /// <summary>面向用户显示的完整表名。</summary>
    public string DisplayName => $"{Schema}.{Name}";
}

/// <summary>
/// 在目标数据库完整模型上替换选中表的设计。其他表、视图、存储过程等沿用实际定义，
/// 不通过过滤整库报告来模拟单表部署，也不把其他设计表带入同步包。
/// </summary>
public static class TableDeployment
{
    /// <summary>构建只包含选中表设计变化的部署包，模型操作均在内存中执行。</summary>
    public static byte[] BuildPackage(DesignProject project, TableDeploymentScope scope, byte[] targetPackage)
    {
        var design = project.Tables.SingleOrDefault(table => table.Id == scope.TableId)
            ?? throw new InvalidOperationException("当前表已不存在，请刷新项目。");
        if (design.Schema != scope.Schema || design.Name != scope.Name)
        {
            throw new InvalidOperationException("当前表名称已变化，请重新比对。");
        }
        var script = SqlServerDdl.Generate(project, design, forModel: true);
        using var input = new MemoryStream(targetPackage);
        using var model = TSqlModel.LoadFromDacpac(input, new ModelLoadOptions { LoadAsScriptBackedModel = true });
        var target = model.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass)
            .SingleOrDefault(table => IsTable(table, scope));
        var sourceName = "DbStudio.SelectedTable.sql";
        if (target != null)
        {
            // 表声明包含字段、主键及内联约束；单独声明的索引、外键需分别替换。
            var descriptions = model.GetObjects(DacQueryScopes.UserDefined, ExtendedProperty.TypeClass)
                .Where(property => property.Name.Parts.LastOrDefault() == "MS_Description"
                    && property.GetReferenced(ExtendedProperty.Host).Any(host =>
                        host.ObjectType.Name == "Table" && IsTable(host, scope)
                        || host.ObjectType.Name == "Column" && host.GetParent() is { } parent && IsTable(parent, scope)))
                .ToList();
            sourceName = target.GetSourceInformation().SourceName;
            var children = target.GetChildren().Where(child => child.ObjectType.Name is
                "Index" or "PrimaryKeyConstraint" or "UniqueConstraint" or "CheckConstraint" or "DefaultConstraint" or "ForeignKeyConstraint").ToList();
            var removeSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in children.Concat(descriptions))
            {
                if (child.GetSourceInformation()?.SourceName == sourceName) { continue; }
                var childSource = child.GetSourceInformation()?.SourceName
                    ?? throw new InvalidOperationException($"无法安全替换当前表的 {child.ObjectType.Name}，请使用整库比对。");
                removeSources.Add(childSource);
            }
            foreach (var name in removeSources) { model.DeleteObjects(name); }
        }
        if (!scope.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)
            && !model.GetObjects(DacQueryScopes.UserDefined, Schema.TypeClass)
                .Any(schema => schema.Name.Parts.Last().Equals(scope.Schema, StringComparison.OrdinalIgnoreCase)))
        {
            model.AddObjects($"CREATE SCHEMA {SqlServerDdl.Q(scope.Schema)};");
        }
        model.AddOrUpdateObjects(script, sourceName, new TSqlObjectOptions());
        using var output = new MemoryStream();
        DacPackageExtensions.BuildPackage(output, model, new PackageMetadata { Name = "DbStudioTableDesign", Version = "1.0.0.0" });
        return output.ToArray();
    }

    /// <summary>
    /// 对完整部署报告做范围检查。涉及其他表或视图的依赖变更时拒绝单表执行，
    /// 用户需回到整库工具核对关联对象，不能静默隐藏这些操作。
    /// </summary>
    public static void ValidateChanges(byte[] sourcePackage, byte[] targetPackage, TableDeploymentScope scope, IReadOnlyList<SchemaChange> changes)
    {
        var allowed = new HashSet<(string Type, string Name)>();
        var anonymous = new HashSet<(string Type, string Name)>();
        foreach (var bytes in new[] { sourcePackage, targetPackage })
        {
            using var input = new MemoryStream(bytes);
            using var model = TSqlModel.LoadFromDacpac(input, new ModelLoadOptions());
            var objects = model.GetObjects(DacQueryScopes.UserDefined).ToList();
            var table = objects.SingleOrDefault(item => IsTable(item, scope));
            if (table != null) { objects.AddRange(table.GetChildren()); }
            foreach (var item in objects.Where(item => BelongsTo(item, scope)))
            {
                allowed.Add(("Sql" + item.ObjectType.Name, item.Name.ToString()));
                // 部署报告对匿名约束使用本地化显示名，交给 DacFx 自身生成，避免按语言猜测。
                allowed.Add(("Sql" + item.ObjectType.Name, model.DisplayServices.GetElementName(item, ElementNameStyle.EscapedFullyQualifiedName)));
                if (item.ObjectType.Name == "ExtendedProperty")
                {
                    // 扩展属性在模型标识中带宿主类型前缀，部署报告省略该前缀。
                    allowed.Add(("SqlExtendedProperty", string.Join(".", item.Name.Parts.Skip(1).Select(SqlServerDdl.Q))));
                }
                if (item.ObjectType.Name == "DefaultConstraint" && item.Name.Parts.Count == 0)
                {
                    anonymous.Add(("SqlDefaultConstraint", model.DisplayServices.GetElementName(item, ElementNameStyle.EscapedFullyQualifiedName)));
                    allowed.Add(("SqlDefaultConstraint", $"{SqlServerDdl.Q(scope.Schema)}.{SqlServerDdl.Q(scope.Name)}"));
                }
            }
        }
        var outside = changes.Where(change => !allowed.Contains((change.ObjectType, change.Name))
            // 新表的匿名 DEFAULT 在报告中可能带本地化类型前缀；仍严格匹配类型及完整宿主显示名。
            && !anonymous.Any(item => item.Type == change.ObjectType && change.Name.EndsWith(": " + item.Name, StringComparison.Ordinal))
            && !(change.Operation == "Create" && change.ObjectType == "SqlSchema" && change.Name == SqlServerDdl.Q(scope.Schema)))
            .ToList();
        if (outside.Count > 0)
        {
            throw new InvalidOperationException("本次变更涉及当前表之外的依赖对象，单表同步已停止。请使用整库比对核对关联影响：\n"
                + string.Join("\n", outside.Take(8).Select(change => $"{change.Operation} · {change.ObjectType} · {change.Name}")));
        }
    }

    /// <summary>按 DacFx 的宿主关系判断归属，不把其他表指向当前表的外键算作本表约束。</summary>
    private static bool BelongsTo(TSqlObject item, TableDeploymentScope scope)
    {
        if (item.ObjectType.Name == "ExtendedProperty")
        {
            return item.GetReferenced(ExtendedProperty.Host).Any(host => BelongsTo(host, scope));
        }
        for (TSqlObject? current = item; current != null; current = current.GetParent())
        {
            if (current.ObjectType.Name == "Table") { return IsTable(current, scope); }
        }
        return false;
    }

    /// <summary>比较模型中的完整标识符，避免以字符串前缀误匹配同名或相似名称的表。</summary>
    private static bool IsTable(TSqlObject table, TableDeploymentScope scope) => table.ObjectType.Name == "Table"
        && table.Name.Parts.Count == 2
        && table.Name.Parts[0].Equals(scope.Schema, StringComparison.OrdinalIgnoreCase)
        && table.Name.Parts[1].Equals(scope.Name, StringComparison.OrdinalIgnoreCase);
}
