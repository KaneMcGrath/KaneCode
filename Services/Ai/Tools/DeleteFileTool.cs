using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that deletes a file by path.
/// Supports both absolute paths and paths relative to the loaded project root.
/// </summary>
internal sealed class DeleteFileTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to the file to delete. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
                }
            },
            "required": ["filePath"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public DeleteFileTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "delete_file";

    public string Description => "Delete a file by path.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("filePath", out JsonElement filePathElement) ||
            string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        string filePath = filePathElement.GetString()!.Trim();
        string resolvedPath;

        try
        {
            resolvedPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, filePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (Directory.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"Path is a directory, not a file: {filePath}"));
        }

        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"File not found: {filePath}"));
        }

        try
        {
            File.Delete(resolvedPath);
            return Task.FromResult(ToolCallResult.Ok($"Deleted file '{resolvedPath}'."));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error deleting file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
    }
}
