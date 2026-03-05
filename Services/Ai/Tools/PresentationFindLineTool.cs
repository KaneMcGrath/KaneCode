using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that finds the first line in a file containing a search string.
/// Supports absolute paths, project-relative paths, or a file name lookup.
/// </summary>
internal sealed class PresentationFindLineTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "file": {
                    "type": "string",
                    "description": "File path or file name to search in. Can be absolute, relative to project root, or just a file name."
                },
                "searchString": {
                    "type": "string",
                    "description": "The text to locate in the file."
                }
            },
            "required": ["file", "searchString"]
        }
        """).RootElement.Clone();

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages"
    };

    private readonly Func<string?> _projectRootProvider;

    public PresentationFindLineTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "presentation_find_line";

    public string Description =>
        "Finds the first line in a file containing the provided search string. " +
        "Use this before presentation_add_slide to get an exact line number for highlighting.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("file", out JsonElement fileElement) ||
            string.IsNullOrWhiteSpace(fileElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: file"));
        }

        if (!arguments.TryGetProperty("searchString", out JsonElement searchElement) ||
            string.IsNullOrWhiteSpace(searchElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: searchString"));
        }

        string fileInput = fileElement.GetString()!.Trim();
        string searchString = searchElement.GetString()!.Trim();

        string? resolvedPath = ResolveFilePath(fileInput);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"File not found: {fileInput}"));
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(resolvedPath);
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error reading file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(searchString, StringComparison.OrdinalIgnoreCase))
            {
                int lineNumber = i + 1;
                string snippet = lines[i].Trim();
                return Task.FromResult(ToolCallResult.Ok(
                    $"Found in {resolvedPath} at line {lineNumber}: {snippet}"));
            }
        }

        return Task.FromResult(ToolCallResult.Fail(
            $"Search string not found in file: {searchString}"));
    }

    private string? ResolveFilePath(string fileInput)
    {
        if (Path.IsPathRooted(fileInput))
        {
            return fileInput;
        }

        string? root = _projectRootProvider();
        if (string.IsNullOrWhiteSpace(root))
        {
            return fileInput;
        }

        if (File.Exists(root))
        {
            root = Path.GetDirectoryName(root);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return fileInput;
        }

        string combinedPath = Path.Combine(root, fileInput);
        if (File.Exists(combinedPath))
        {
            return combinedPath;
        }

        if (fileInput.Contains(Path.DirectorySeparatorChar) || fileInput.Contains(Path.AltDirectorySeparatorChar))
        {
            return combinedPath;
        }

        try
        {
            IEnumerable<string> matches = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !IsInSkippedDirectory(path))
                .Where(path => string.Equals(Path.GetFileName(path), fileInput, StringComparison.OrdinalIgnoreCase));

            return matches.FirstOrDefault();
        }
        catch
        {
            return combinedPath;
        }
    }

    private static bool IsInSkippedDirectory(string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string part in parts)
        {
            if (SkippedDirectories.Contains(part))
            {
                return true;
            }
        }

        return false;
    }
}
