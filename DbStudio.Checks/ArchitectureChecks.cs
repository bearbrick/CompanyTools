using System.Text.RegularExpressions;

/// <summary>
/// 将已确立的代码组织和中文文档约定固化为可重复检查。
/// </summary>
internal static partial class ArchitectureChecks
{
    /// <summary>
    /// 检查关键组合类的文件规模、XML 文档开关和中文摘要。
    /// </summary>
    internal static void Run(string root, Action<string, bool> check)
    {
        var projectFile = File.ReadAllText(Path.Combine(root, "DbStudio.csproj"));
        check("Public XML documentation is enforced by the product build",
            projectFile.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", StringComparison.Ordinal)
            && projectFile.Contains("CS1573;CS1591", StringComparison.Ordinal));

        check("Home page orchestration stays split below 400 lines",
            File.ReadLines(Path.Combine(root, "Components", "Pages", "Home.razor.cs")).Count() < 400);
        check("Version storage responsibilities stay in separate partial files",
            Directory.GetFiles(Path.Combine(root, "Core"), "StudioStore.Versioning.*.cs").Length >= 3
            && Directory.GetFiles(Path.Combine(root, "Core"), "StudioStore.Versioning.*.cs")
                .All(path => File.ReadLines(path).Count() < 220));

        var productionFiles = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor")
            .Where(path => !IsGeneratedOrVendor(path))
            .ToArray();
        check("Product source files stay below the architecture size ceiling",
            productionFiles.All(path => File.ReadLines(path).Count() < 500));

        var sourceFiles = productionFiles.Where(path => Path.GetExtension(path) == ".cs");
        var summaries = sourceFiles.SelectMany(path => SummaryRegex().Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value));
        check("XML documentation summaries use Chinese product language",
            summaries.All(summary => ChineseRegex().IsMatch(summary)));

        var commentedSources = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".js")
            .Where(path => !IsGeneratedOrVendor(path));
        check("First-party line comments use Chinese product language",
            commentedSources.All(path => !EnglishOnlyLineCommentRegex().IsMatch(File.ReadAllText(path))));
    }

    private static bool IsGeneratedOrVendor(string path)
    {
        return path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"<summary>([\s\S]*?)</summary>", RegexOptions.Compiled)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"[\u3400-\u9fff]", RegexOptions.Compiled)]
    private static partial Regex ChineseRegex();

    [GeneratedRegex(@"^\s*//(?!/)(?=[^\r\n]*[A-Za-z])(?:(?![\u3400-\u9fff])[^\r\n])*$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex EnglishOnlyLineCommentRegex();
}
