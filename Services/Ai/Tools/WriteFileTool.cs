using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that creates or overwrites a file by path.
/// Supports both absolute paths and paths relative to the loaded project root.
/// </summary>
internal sealed class WriteFileTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to the file to write. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
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
    private readonly Action<string>? _onFileChanged;

    public WriteFileTool(Func<string?> projectRootProvider, Action<string>? onFileChanged = null)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
        _onFileChanged = onFileChanged;
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

        string filePath = filePathElement.GetString()!.Trim();
        string content = contentElement.GetString() ?? string.Empty;
        string resolvedPath;

        try
        {
            resolvedPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, filePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        try
        {
            string? directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(resolvedPath, content);
            _onFileChanged?.Invoke(resolvedPath);

            int bytes = System.Text.Encoding.UTF8.GetByteCount(content);
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

}
