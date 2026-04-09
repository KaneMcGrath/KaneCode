using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that renames or moves a file system entry by path.
/// Supports both absolute paths and paths relative to the loaded project root.
/// </summary>
internal sealed class RenamePathTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "sourcePath": {
                    "type": "string",
                    "description": "The existing file or directory path to rename. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
                },
                "destinationPath": {
                    "type": "string",
                    "description": "The new file or directory path. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
                }
            },
            "required": ["sourcePath", "destinationPath"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public RenamePathTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "rename_path";

    public string Description => "Rename or move a file or directory to a new path inside the loaded project.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("sourcePath", out JsonElement sourcePathElement) ||
            string.IsNullOrWhiteSpace(sourcePathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: sourcePath"));
        }

        if (!arguments.TryGetProperty("destinationPath", out JsonElement destinationPathElement) ||
            string.IsNullOrWhiteSpace(destinationPathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: destinationPath"));
        }

        string sourcePath = sourcePathElement.GetString()!.Trim();
        string destinationPath = destinationPathElement.GetString()!.Trim();
        string resolvedSourcePath;
        string resolvedDestinationPath;
        string projectRoot;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            projectRoot = AgentToolPathResolver.GetProjectRootDirectory(_projectRootProvider);
            resolvedSourcePath = AgentToolPathResolver.ResolvePath(_projectRootProvider, sourcePath);
            resolvedDestinationPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, destinationPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (string.Equals(
            Path.TrimEndingDirectorySeparator(resolvedSourcePath),
            Path.TrimEndingDirectorySeparator(projectRoot),
            pathComparison) &&
            Directory.Exists(resolvedSourcePath))
        {
            return Task.FromResult(ToolCallResult.Fail("Renaming the loaded project root directory is not allowed."));
        }

        if (string.Equals(resolvedSourcePath, resolvedDestinationPath, pathComparison))
        {
            return Task.FromResult(ToolCallResult.Fail("Source and destination paths must be different."));
        }

        bool sourceIsFile = File.Exists(resolvedSourcePath);
        bool sourceIsDirectory = Directory.Exists(resolvedSourcePath);

        if (!sourceIsFile && !sourceIsDirectory)
        {
            return Task.FromResult(ToolCallResult.Fail($"Path not found: {sourcePath}"));
        }

        if (File.Exists(resolvedDestinationPath) || Directory.Exists(resolvedDestinationPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"Destination already exists: {destinationPath}"));
        }

        string? destinationDirectory = Path.GetDirectoryName(resolvedDestinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            return Task.FromResult(ToolCallResult.Fail($"Destination directory not found: {destinationPath}"));
        }

        try
        {
            if (sourceIsFile)
            {
                File.Move(resolvedSourcePath, resolvedDestinationPath);
                return Task.FromResult(ToolCallResult.Ok($"Renamed file '{resolvedSourcePath}' to '{resolvedDestinationPath}'."));
            }

            Directory.Move(resolvedSourcePath, resolvedDestinationPath);
            return Task.FromResult(ToolCallResult.Ok($"Renamed directory '{resolvedSourcePath}' to '{resolvedDestinationPath}'."));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error renaming path: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
    }
}
