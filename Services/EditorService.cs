using ICSharpCode.AvalonEdit.Highlighting;
using KaneCode.Models;
using KaneCode.Theming;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// Provides file I/O and syntax-highlighting resolution for the editor.
/// </summary>
internal static class EditorService
{
    /// <summary>Well-known directories and file patterns to exclude from the project tree.</summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", "node_modules", "packages", ".idea"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".suo", ".user", ".ds_store"
    };

    /// <summary>
    /// Reads the full text of a file.
    /// </summary>
    public static string ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Writes text content to a file, creating intermediate directories if needed.
    /// </summary>
    public static void WriteFile(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Returns the AvalonEdit syntax-highlighting name for a given file extension.
    /// </summary>
    public static string? GetSyntaxHighlighting(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "C#",
            ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".config" or ".resx" => "XML",
            ".json" => "Json",
            ".js" => "JavaScript",
            ".html" or ".htm" => "HTML",
            ".css" => "CSS",
            ".sql" => "TSQL",
            ".md" => "MarkDown",
            ".py" => "Python",
            ".cpp" or ".c" or ".h" or ".hpp" => "C++",
            ".java" => "Java",
            ".vb" => "VB",
            _ => null
        };
    }

    /// <summary>
    /// Applies themed syntax colors to an AvalonEdit highlighting definition.
    /// </summary>
    public static void ApplySyntaxHighlightingTheme(IHighlightingDefinition? highlighting)
    {
        if (highlighting is null)
        {
            return;
        }

        foreach (var color in highlighting.NamedHighlightingColors)
        {
            var resourceKey = GetSyntaxResourceKey(color.Name);
            if (Application.Current.TryFindResource(resourceKey) is SolidColorBrush brush)
            {
                color.Foreground = new SimpleHighlightingBrush(brush.Color);
            }
        }
    }

    private static string GetSyntaxResourceKey(string? highlightingName)
    {
        if (string.IsNullOrWhiteSpace(highlightingName))
        {
            return ThemeResourceKeys.SyntaxDefaultForeground;
        }

        if (highlightingName.Contains("Comment", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeResourceKeys.SyntaxCommentForeground;
        }

        if (highlightingName.Contains("Keyword", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeResourceKeys.SyntaxKeywordForeground;
        }

        if (highlightingName.Contains("String", StringComparison.OrdinalIgnoreCase)
            || highlightingName.Contains("Char", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeResourceKeys.SyntaxStringForeground;
        }

        if (highlightingName.Contains("Number", StringComparison.OrdinalIgnoreCase)
            || highlightingName.Contains("Digit", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeResourceKeys.SyntaxNumberForeground;
        }

        if (highlightingName.Contains("Preprocessor", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeResourceKeys.SyntaxPreprocessorForeground;
        }

        if (highlightingName.Contains("Type", StringComparison.OrdinalIgnoreCase)
            || highlightingName.Contains("Class", StringComparison.OrdinalIgnoreCase)
            || highlightingName.Contains("Struct", StringComparison.OrdinalIgnoreCase)
            || highlightingName.Contains("Enum", StringComparison.OrdinalIgnoreCase)
            || highlightingName.Contains("Interface", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeResourceKeys.SyntaxTypeForeground;
        }

        return ThemeResourceKeys.SyntaxDefaultForeground;
    }

    /// <summary>
    /// Builds a tree of <see cref="ProjectItem"/> nodes for the given root directory.
    /// </summary>
    public static ProjectItem BuildFileTree(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        var rootInfo = new DirectoryInfo(rootPath);
        var root = new ProjectItem(rootInfo.FullName, isDirectory: true) { IsExpanded = true };
        PopulateChildren(root, rootInfo);
        return root;
    }

    private static void PopulateChildren(ProjectItem parent, DirectoryInfo dirInfo)
    {
        try
        {
            foreach (var dir in dirInfo.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ExcludedDirectories.Contains(dir.Name))
                {
                    continue;
                }

                var dirItem = new ProjectItem(dir.FullName, isDirectory: true);
                PopulateChildren(dirItem, dir);
                parent.Children.Add(dirItem);
            }

            foreach (var file in dirInfo.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ExcludedExtensions.Contains(file.Extension))
                {
                    continue;
                }

                parent.Children.Add(new ProjectItem(file.FullName, isDirectory: false));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
    }
}
