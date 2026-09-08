using System.Xml.Linq;

namespace DbStudio.Core;

/// <summary>诊断涉及的数据库对象，来自 DacFx 报告中的对象名称和类型。</summary>
public record WarningObject(string Name, string Type);

/// <summary>一条具体诊断；保留原文及问题编号，关联到报告中的变更对象。</summary>
public record DeploymentIssue(string Id, string Message, List<WarningObject> Objects);

/// <summary>按 DacFx 警告类型组织的中文说明、具体诊断和可核对的原始报告片段。</summary>
public record DeploymentWarning(string Code, string Title, string Explanation, List<DeploymentIssue> Issues, string RawXml);

/// <summary>解析部署报告的属性式诊断。只补充分类说明，不推断具体数据是否已经丢失。</summary>
public static class DeploymentWarnings
{
    /// <summary>
    /// Issue.Value 保存诊断原文；Issue.Id 可关联 Operations/Item 下同编号的问题。
    /// 同时兼容文本式诊断、内嵌对象及未知警告，原始 XML 始终保留供核对。
    /// </summary>
    public static List<DeploymentWarning> Parse(XDocument report)
    {
        var operationItems = report.Descendants().Where(e => e.Name.LocalName == "Operation")
            .SelectMany(e => e.Descendants().Where(item => item.Name.LocalName == "Item")).ToList();
        return report.Descendants().Where(e => e.Name.LocalName == "Alert").Select(alert =>
        {
            var code = (string?)alert.Attribute("Name") ?? "Unknown";
            var (title, explanation) = Describe(code);
            var issues = alert.Descendants().Where(e => e.Name.LocalName == "Issue").Select(issue =>
            {
                var id = (string?)issue.Attribute("Id") ?? "";
                var objects = issue.Descendants().Where(e => e.Name.LocalName == "Item")
                    .Concat(operationItems.Where(item => id != "" && item.Descendants().Any(e => e.Name.LocalName == "Issue" && (string?)e.Attribute("Id") == id)))
                    .Select(item => new WarningObject((string?)item.Attribute("Value") ?? "", (string?)item.Attribute("Type") ?? ""))
                    .Where(item => item.Name != "").Distinct().ToList();
                return new DeploymentIssue(id, Message(issue), objects);
            }).ToList();
            if (issues.Count == 0)
            {
                issues.Add(new("", Message(alert), []));
            }
            return new DeploymentWarning(code, title, explanation, issues, alert.ToString());
        }).ToList();
    }

    private static string Message(XElement element)
    {
        var value = (string?)element.Attribute("Value");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        var message = (string?)element.Attribute("Message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }
        return string.IsNullOrWhiteSpace(element.Value) ? "报告未提供具体说明，请展开原始诊断并核对同步 SQL。" : element.Value.Trim();
    }

    /// <summary>分类解释与原文分开展示，避免把通用说明误认为本次数据库的事实。</summary>
    private static (string Title, string Explanation) Describe(string code) => code switch
    {
        "CreateClusteredIndex" => ("创建聚集索引", "计划包含创建聚集索引。已有数据可能需要重新组织，请核对下列索引和表，并评估执行耗时及锁表影响。"),
        "DropClusteredIndex" => ("删除聚集索引", "计划包含删除聚集索引，请核对受影响对象及数据组织方式的变化。"),
        "DataIssue" => ("数据兼容性风险", "部分结构变更可能影响现有数据或导致部署失败。请逐项核对下方具体原因；此提示不代表已经发生数据丢失。"),
        "DataMotion" => ("数据迁移或表重建", "计划可能通过移动数据或重建表完成变更，请核对相关对象及执行脚本。"),
        _ => ("其他部署提示", "DacFx 返回了以下诊断，请结合具体原因和同步 SQL 核对。")
    };
}
