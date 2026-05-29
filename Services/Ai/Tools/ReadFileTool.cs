using KaneCode.Services.Ai;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that reads a file's contents by path.
/// Supports both absolute paths and paths relative to the loaded project root.
/// Enforces a maximum line count to protect the context budget.
/// Supports reading a specific range of lines via optional startLine/endLine parameters.
/// </summary>
internal sealed class ReadFileTool : IAgentTool
{
    /// <summary>Maximum number of lines returned when no explicit line range is requested.</summary>
    private const int MaxLines = 2000;

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
                },
                "startLine": {
                    "type": "integer",
                    "description": "Optional 1-based starting line number. When specified, returns content from this line onward (up to endLine if also specified)."
                },
                "endLine": {
                    "type": "integer",
                    "description": "Optional 1-based ending line number (inclusive). When specified, returns content up to this line. Must be greater than or equal to startLine if both are provided."
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

    public string Description => "Read the contents of a file by path. Returns the file text or an error if the file is not found or exceeds the maximum line count. Supports files inside the loaded project and request-scoped external context folders. Optionally accepts startLine and endLine (1-based, inclusive) to read a specific range of lines.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!TryGetRequestedFilePaths(arguments, out IReadOnlyList<string>? requestedFilePaths))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath or filePaths"));
        }

        int? startLine = TryGetOptionalInt32(arguments, "startLine");
        int? endLine = TryGetOptionalInt32(arguments, "endLine");

        // Validate line range parameters
        if (startLine.HasValue && startLine.Value < 1)
        {
            return Task.FromResult(ToolCallResult.Fail("startLine must be a positive integer (1-based)."));
        }

        if (endLine.HasValue && endLine.Value < 1)
        {
            return Task.FromResult(ToolCallResult.Fail("endLine must be a positive integer (1-based)."));
        }

        if (startLine.HasValue && endLine.HasValue && startLine.Value > endLine.Value)
        {
            return Task.FromResult(ToolCallResult.Fail("startLine must be less than or equal to endLine."));
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

            try
            {
                string[] allLines = File.ReadAllLines(resolvedPath);
                string content;

                if (startLine.HasValue || endLine.HasValue)
                {
                    // 1-based line numbers; clamp to valid range
                    int start = Math.Max(1, startLine ?? 1);
                    int end = Math.Min(allLines.Length, endLine ?? allLines.Length);

                    if (start > allLines.Length)
                    {
                        content = string.Empty;
                    }
                    else
                    {
                        int length = end - start + 1;
                        content = string.Join(Environment.NewLine, allLines, start - 1, length);
                    }
                }
                else
                {
                    // No explicit range — apply MaxLines cap to protect context budget
                    if (allLines.Length > MaxLines)
                    {
                        content = string.Join(Environment.NewLine, allLines, 0, MaxLines);
                        content += Environment.NewLine + Environment.NewLine +
                            $"[Content was trimmed to {MaxLines} lines. The full file has {allLines.Length} lines. Use startLine/endLine parameters to read a specific range.]";
                    }
                    else
                    {
                        content = string.Join(Environment.NewLine, allLines);
                    }
                }

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

    private static int? TryGetOptionalInt32(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out int value))
        {
            return value;
        }

        return null;
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
