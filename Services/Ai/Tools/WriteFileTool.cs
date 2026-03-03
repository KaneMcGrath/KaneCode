using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that creates or overwrites a file by path.
/// Supports both absolute paths and paths relative to the project root.
/// </summary>
internal sealed class WriteFileTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to the file to write. Can be absolute or relative to the project root."
                },
                "content": {
                    "type": "string",
                    "description": "The full content to write into the file."
                }
            },
            "required": ["filePath", "content"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public WriteFileTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "write_file";

    public string Description => "Create or overwrite a file by path with provided content.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("filePath", out var filePathElement) ||
            string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        if (!arguments.TryGetProperty("content", out var contentElement))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: content"));
        }

        var filePath = filePathElement.GetString()!.Trim();
        var content = contentElement.GetString() ?? string.Empty;
        var resolvedPath = ResolvePath(filePath);

        try
        {
            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(resolvedPath, content);

            var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            return Task.FromResult(ToolCallResult.Ok(
                $"Wrote {bytes} bytes to '{resolvedPath}'."));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error writing file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Invalid path: {ex.Message}"));
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Unsupported path: {ex.Message}"));
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
