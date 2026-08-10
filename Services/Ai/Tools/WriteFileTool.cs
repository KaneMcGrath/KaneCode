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

    private static readonly JsonElement BackendOptions = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "require_confirmation": {
                    "type": "boolean",
                    "default": true,
                    "description": "Require user confirmation before writing the file"
                },
                "timeout": {
                    "type": "integer",
                    "default": 30,
                    "minimum": 1,
                    "maximum": 600,
                    "description": "Execution timeout in seconds"
                },
                "path_scope": {
                    "type": "string",
                    "enum": ["project", "project_external"],
                    "default": "project",
                    "description": "Where file paths may resolve: project only, or project plus attached external context folders"
                }
            }
        }
        """).RootElement.Clone();

    private static readonly JsonElement DefaultOptions = JsonDocument.Parse("""
        {
            "require_confirmation": true,
            "timeout": 30,
            "path_scope": "project"
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;
    private readonly Action<string>? _onFileChanged;
    private readonly ExternalContextDirectoryRegistry? _externalContextDirectoryRegistry;

    public WriteFileTool(
        Func<string?> projectRootProvider,
        Action<string>? onFileChanged = null,
        ExternalContextDirectoryRegistry? externalContextDirectoryRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
        _onFileChanged = onFileChanged;
        _externalContextDirectoryRegistry = externalContextDirectoryRegistry;
    }

    public string Name => "write";

    public string Category => "Write Files";

    public string Description => "Create or overwrite a file by path with provided content.";

    public JsonElement ParametersSchema => Schema;

    public JsonElement BackendOptionsSchema => BackendOptions;

    public IReadOnlyDictionary<string, JsonElement> DefaultBackendOptions
    {
        get
        {
            Dictionary<string, JsonElement> defaults = new(StringComparer.Ordinal);
            foreach (JsonProperty property in DefaultOptions.EnumerateObject())
            {
                defaults[property.Name] = property.Value.Clone();
            }

            return defaults;
        }
    }

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

        string pathScope = AgentToolContext.GetString("path_scope", "project");

        try
        {
            resolvedPath = pathScope == "project_external" && _externalContextDirectoryRegistry is not null
                ? AgentToolPathResolver.ResolvePath(
                    _projectRootProvider,
                    filePath,
                    _externalContextDirectoryRegistry.GetAllowedDirectories())
                : AgentToolPathResolver.ResolvePath(_projectRootProvider, filePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        try
        {
            bool existedBeforeWrite = File.Exists(resolvedPath);

            string? directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(resolvedPath, content);
            _onFileChanged?.Invoke(resolvedPath);

            int bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            int chars = content.Length;
            int lineCount = ToolResultDetails.CountLines(content);
            string summary = $"Wrote {bytes} bytes to '{resolvedPath}'.";

            string? projectRoot = GetProjectRootSafely();
            string displayPath = ToolResultDetails.GetDisplayPath(resolvedPath, projectRoot);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(existedBeforeWrite ? $"Overwrote '{displayPath}'" : $"Created '{displayPath}'");
            sb.AppendLine($"{ToolResultDetails.FormatByteSize(bytes)} · {chars:N0} chars · {lineCount:N0} lines");

            string? preview = ToolResultDetails.BuildContentPreview(content);
            if (preview is not null)
            {
                sb.AppendLine();
                sb.AppendLine("Preview:");
                sb.AppendLine(preview);
            }

            return Task.FromResult(ToolCallResult.OkWithDetails(summary, sb.ToString().TrimEnd()));
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

    /// <summary>
    /// Resolves the project root for display purposes, or null when no project
    /// is loaded (e.g. direct tool invocation in tests). Never throws.
    /// </summary>
    private string? GetProjectRootSafely()
    {
        try
        {
            return AgentToolPathResolver.GetProjectRootDirectory(_projectRootProvider);
        }
        catch
        {
            return null;
        }
    }

}
