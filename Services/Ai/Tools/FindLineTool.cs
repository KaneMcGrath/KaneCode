using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that finds the first line in a file containing a search string.
/// Supports absolute paths, project-relative paths, or a file name lookup.
/// </summary>
internal sealed class FindLineTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "file": {
                    "type": "string",
                    "description": "File path or file name to search in. Can be absolute, relative to the loaded project root, or just a file name, but must stay inside the loaded project."
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

    public FindLineTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "find_line";

    public string Description =>
        "Finds the first line in a file containing the provided search string.";

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

        string resolvedPath;
        try
        {
            resolvedPath = AgentToolPathResolver.ResolveFilePathOrFileName(_projectRootProvider, fileInput, SkippedDirectories);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

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

}
