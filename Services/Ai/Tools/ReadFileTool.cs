using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that reads a file's contents by path.
/// Supports both absolute paths and paths relative to the project root.
/// Enforces a max file size to protect the context budget.
/// </summary>
internal sealed class ReadFileTool : IAgentTool
{
    private const int MaxFileSizeBytes = 100 * 1024; // 100 KB

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to the file to read. Can be absolute or relative to the project root."
                }
            },
            "required": ["filePath"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public ReadFileTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "read_file";

    public string Description => "Read the contents of a file by path. Returns the file text or an error if the file is not found or too large (>100 KB).";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("filePath", out var filePathElement) ||
            string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        var filePath = filePathElement.GetString()!.Trim();
        var resolvedPath = ResolvePath(filePath);

        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"File not found: {filePath}"));
        }

        var fileInfo = new FileInfo(resolvedPath);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            return Task.FromResult(ToolCallResult.Fail(
                $"File too large ({fileInfo.Length / 1024} KB). Maximum is {MaxFileSizeBytes / 1024} KB."));
        }

        try
        {
            var content = File.ReadAllText(resolvedPath);
            return Task.FromResult(ToolCallResult.Ok(content));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error reading file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
    }

    private string ResolvePath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        var root = _projectRootProvider();
        if (string.IsNullOrWhiteSpace(root))
        {
            return filePath;
        }

        // Root may point to a .sln or .csproj file — use its directory
        if (File.Exists(root))
        {
            root = Path.GetDirectoryName(root);
        }

        return string.IsNullOrWhiteSpace(root)
            ? filePath
            : Path.GetFullPath(Path.Combine(root, filePath));
    }
}
