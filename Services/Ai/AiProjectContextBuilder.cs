using KaneCode.Models;
using System.IO;
using System.Xml.Linq;

namespace KaneCode.Services.Ai;

/// <summary>
/// Builds a compact project-wide context string (file tree, target frameworks, and packages)
/// suitable for injecting as a system message in AI conversations.
/// </summary>
internal static class AiProjectContextBuilder
{
    private const int MaxTreeLines = 250;

    /// <summary>
    /// Builds project context from the current project tree.
    /// </summary>
    internal static string Build(IReadOnlyList<ProjectItem> rootItems)
    {
        ArgumentNullException.ThrowIfNull(rootItems);

        if (rootItems.Count == 0)
        {
            return string.Empty;
        }

        var treeLines = new List<string>();
        foreach (var item in rootItems)
        {
            AppendTree(item, depth: 0, treeLines);
            if (treeLines.Count >= MaxTreeLines)
            {
                break;
            }
        }

        var projectFiles = new List<string>();
        CollectProjectFiles(rootItems, projectFiles);

        var tfmLines = new List<string>();
        var packageLines = new List<string>();

        foreach (var projectFile in projectFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ExtractProjectMetadata(projectFile, tfmLines, packageLines);
            }
            catch (IOException ex)
            {
                tfmLines.Add($"- {Path.GetFileName(projectFile)}: metadata unavailable ({ex.Message})");
            }
            catch (UnauthorizedAccessException ex)
            {
                tfmLines.Add($"- {Path.GetFileName(projectFile)}: metadata unavailable ({ex.Message})");
            }
            catch (System.Xml.XmlException ex)
            {
                tfmLines.Add($"- {Path.GetFileName(projectFile)}: invalid project XML ({ex.Message})");
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Project-wide context:");
        sb.AppendLine();

        if (tfmLines.Count > 0)
        {
            sb.AppendLine("Target frameworks:");
            foreach (var line in tfmLines)
            {
                sb.AppendLine(line);
            }

            sb.AppendLine();
        }

        if (packageLines.Count > 0)
        {
            sb.AppendLine("Packages:");
            foreach (var line in packageLines.Distinct(StringComparer.OrdinalIgnoreCase).Take(200))
            {
                sb.AppendLine(line);
            }

            sb.AppendLine();
        }

        sb.AppendLine("File tree:");
        foreach (var line in treeLines.Take(MaxTreeLines))
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static void AppendTree(ProjectItem item, int depth, List<string> lines)
    {
        if (lines.Count >= MaxTreeLines)
        {
            return;
        }

        var prefix = new string(' ', depth * 2);
        lines.Add($"{prefix}- {item.Name}");

        foreach (var child in item.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            AppendTree(child, depth + 1, lines);
            if (lines.Count >= MaxTreeLines)
            {
                break;
            }
        }
    }

    private static void CollectProjectFiles(IReadOnlyList<ProjectItem> items, List<string> results)
    {
        foreach (var item in items)
        {
            if (item.ItemType == ProjectItemType.Project &&
                item.FullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(item.FullPath))
            {
                results.Add(item.FullPath);
            }

            if (item.Children.Count > 0)
            {
                CollectProjectFiles(item.Children, results);
            }
        }
    }

    private static void ExtractProjectMetadata(string projectFilePath, List<string> tfmLines, List<string> packageLines)
    {
        var doc = XDocument.Load(projectFilePath, LoadOptions.None);

        var projectName = Path.GetFileName(projectFilePath);
        var tfm = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value?.Trim();
        var tfms = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value?.Trim();

        if (!string.IsNullOrWhiteSpace(tfm))
        {
            tfmLines.Add($"- {projectName}: {tfm}");
        }
        else if (!string.IsNullOrWhiteSpace(tfms))
        {
            tfmLines.Add($"- {projectName}: {tfms}");
        }

        var packageReferences = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e =>
            {
                var include = e.Attribute("Include")?.Value?.Trim();
                var version = e.Attribute("Version")?.Value?.Trim()
                              ?? e.Elements().FirstOrDefault(v => v.Name.LocalName == "Version")?.Value?.Trim();

                return (Include: include, Version: version);
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Include));

        foreach (var package in packageReferences)
        {
            var versionPart = string.IsNullOrWhiteSpace(package.Version) ? "(version unspecified)" : package.Version;
            packageLines.Add($"- {package.Include} {versionPart}");
        }
    }
}
