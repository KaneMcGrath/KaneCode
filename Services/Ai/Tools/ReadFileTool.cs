using KaneCode.Services.Ai;
using System.IO;
using System.Text;
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
                },
                "filePaths": {
                    "type": "array",
                    "description": "Multiple file paths to read in a single call. Each path can be absolute or relative to the loaded project root, or inside an attached external context folder for the current request.",
                    "items": {
                        "type": "string"
                    }
                }
            },
            "anyOf": [
                { "required": ["filePath"] },
                { "required": ["filePaths"] }
            ]
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
        if (!TryGetRequestedFilePaths(arguments, out IReadOnlyList<string>? requestedFilePaths))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath or filePaths"));
        }

        List<(string RequestedPath, string Content)> fileContents = [];
        foreach (string requestedFilePath in requestedFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string resolvedPath;

            try
            {
                if (_externalContextDirectoryRegistry is not null)
                {
                    resolvedPath = AgentToolPathResolver.ResolvePath(
                        _projectRootProvider,
                        requestedFilePath,
                        _externalContextDirectoryRegistry.GetAllowedDirectories());
                }
                else
                {
                    resolvedPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, requestedFilePath);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Task.FromResult(ToolCallResult.Fail(ex.Message));
            }

            if (!File.Exists(resolvedPath))
            {
                return Task.FromResult(ToolCallResult.Fail($"File not found: {requestedFilePath}"));
            }

            FileInfo fileInfo = new(resolvedPath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"File too large ({fileInfo.Length / 1024} KB). Maximum is {MaxFileSizeBytes / 1024} KB."));
            }

            try
            {
                string content = File.ReadAllText(resolvedPath);
                fileContents.Add((requestedFilePath, content));
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

        return Task.FromResult(ToolCallResult.Ok(BuildOutput(fileContents)));
    }

    private static bool TryGetRequestedFilePaths(JsonElement arguments, out IReadOnlyList<string>? filePaths)
    {
        filePaths = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (arguments.TryGetProperty("filePaths", out JsonElement filePathsElement))
        {
            if (filePathsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            List<string> requestedFilePaths = [];
            foreach (JsonElement filePathElement in filePathsElement.EnumerateArray())
            {
                if (filePathElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string? filePath = filePathElement.GetString();
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return false;
                }

                requestedFilePaths.Add(filePath.Trim());
            }

            if (requestedFilePaths.Count == 0)
            {
                return false;
            }

            filePaths = requestedFilePaths;
            return true;
        }

        if (!arguments.TryGetProperty("filePath", out JsonElement filePathValue) ||
            string.IsNullOrWhiteSpace(filePathValue.GetString()))
        {
            return false;
        }

        filePaths = [filePathValue.GetString()!.Trim()];
        return true;
    }

    private static string BuildOutput(IReadOnlyList<(string RequestedPath, string Content)> fileContents)
    {
        if (fileContents.Count == 1)
        {
            return fileContents[0].Content;
        }

        StringBuilder builder = new();
        for (int i = 0; i < fileContents.Count; i++)
        {
            (string requestedPath, string content) = fileContents[i];

            if (i > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append("[File: ");
            builder.Append(requestedPath);
            builder.AppendLine("]");
            builder.Append(content);
        }

        return builder.ToString();
    }

}
