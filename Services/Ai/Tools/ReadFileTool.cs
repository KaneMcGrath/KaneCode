using KaneCode.Services.Ai;
using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that reads a file's contents by path.
/// Supports both absolute paths and paths relative to the loaded project root.
/// Enforces a max file size to protect the context budget.
/// </summary>
internal sealed class ReadFileTool : IAgentTool
{
    private const int MaxFileSizeBytes = 200 * 1024; // 200 KB

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to the file to read. Can be absolute or relative to the loaded project root, or inside an attached external context folder for the current request."
                }
            },
            "required": ["filePath"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;
    private readonly ExternalContextDirectoryRegistry? _externalContextDirectoryRegistry;

    public ReadFileTool(Func<string?> projectRootProvider, ExternalContextDirectoryRegistry? externalContextDirectoryRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
        _externalContextDirectoryRegistry = externalContextDirectoryRegistry;
    }

    public string Name => "read_file";

    public string Description => "Read the contents of a file by path. Returns the file text or an error if the file is not found or too large (>200 KB). Supports files inside the loaded project and request-scoped external context folders.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("filePath", out var filePathElement) ||
            string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        string filePath = filePathElement.GetString()!.Trim();
        string resolvedPath;

        try
        {
            if (_externalContextDirectoryRegistry is not null)
            {
                resolvedPath = AgentToolPathResolver.ResolvePath(
                    _projectRootProvider,
                    filePath,
                    _externalContextDirectoryRegistry.GetAllowedDirectories());
            }
            else
            {
                resolvedPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, filePath);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

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
            string content = File.ReadAllText(resolvedPath);
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

}
