using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that creates a directory by path.
/// Supports both absolute paths and paths relative to the loaded project root.
/// </summary>
internal sealed class CreateDirectoryTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "directoryPath": {
                    "type": "string",
                    "description": "The directory path to create. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
                }
            },
            "required": ["directoryPath"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public CreateDirectoryTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "create_directory";

    public string Description => "Create a directory by path. Intermediate directories are created automatically.";

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
        string resolvedPath;

        try
        {
            resolvedPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, directoryPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"A file already exists at '{resolvedPath}'."));
        }

        try
        {
            DirectoryInfo createdDirectory = Directory.CreateDirectory(resolvedPath);
            return Task.FromResult(ToolCallResult.Ok($"Created directory '{createdDirectory.FullName}'."));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error creating directory: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
    }
}
