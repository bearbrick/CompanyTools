using System.Security.Claims;
using DbStudio.Core;

/// <summary>为原有结构测试的简略字段补充测试定义名；必填规则本身使用真实 SaveTable 单独验证。</summary>
internal static class LabeledDesignFixtures
{
    internal static DesignProject SaveLabeledTable(this StudioStore store, ClaimsPrincipal principal,
        string projectId, int revision, TableDesign table, bool delete = false)
    {
        foreach (var column in table.Columns.Where(c => string.IsNullOrWhiteSpace(c.Label))) { column.Label = "测试" + column.Name; }
        return store.SaveTable(principal, projectId, revision, table, delete);
    }
}
