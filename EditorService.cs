using System.IO;

namespace KaneCode;

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

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
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
            Directory.CreateDirectory(directory);

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
                    continue;

                var dirItem = new ProjectItem(dir.FullName, isDirectory: true);
                PopulateChildren(dirItem, dir);
                parent.Children.Add(dirItem);
            }

            foreach (var file in dirInfo.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ExcludedFiles.Contains(file.Extension))
                    continue;

                parent.Children.Add(new ProjectItem(file.FullName, isDirectory: false));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
    }
}
