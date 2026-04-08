using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that deletes a directory by path.
/// Supports both absolute paths and paths relative to the loaded project root.
/// </summary>
internal sealed class DeleteDirectoryTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "directoryPath": {
                    "type": "string",
                    "description": "The directory path to delete. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
                },
                "recursive": {
                    "type": "boolean",
                    "description": "Set to true to delete a non-empty directory and all of its contents. Defaults to false."
                }
            },
            "required": ["directoryPath"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public DeleteDirectoryTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "delete_directory";

    public string Description => "Delete a directory by path. Use recursive=true to remove non-empty directories.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("directoryPath", out JsonElement directoryPathElement) ||
            string.IsNullOrWhiteSpace(directoryPathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: directoryPath"));
        }

        string directoryPath = directoryPathElement.GetString()!.Trim();
        bool recursive = arguments.TryGetProperty("recursive", out JsonElement recursiveElement) &&
            recursiveElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            recursiveElement.GetBoolean();
        string resolvedPath;
        string projectRoot;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            projectRoot = AgentToolPathResolver.GetProjectRootDirectory(_projectRootProvider);
            resolvedPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, directoryPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (string.Equals(
            Path.TrimEndingDirectorySeparator(resolvedPath),
            Path.TrimEndingDirectorySeparator(projectRoot),
            pathComparison))
        {
            return Task.FromResult(ToolCallResult.Fail("Deleting the loaded project root directory is not allowed."));
        }

        if (File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"Path is a file, not a directory: {directoryPath}"));
        }

        if (!Directory.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"Directory not found: {directoryPath}"));
        }

        try
        {
            Directory.Delete(resolvedPath, recursive);
            return Task.FromResult(ToolCallResult.Ok($"Deleted directory '{resolvedPath}'."));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error deleting directory: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
    }
}
