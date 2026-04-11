using KaneCode.Services.Ai;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that lists files in a directory or the project tree.
/// Returns a flat list of relative file paths.
/// </summary>
internal sealed class ListFilesTool : IAgentTool
{
    private const int MaxFileCount = 2000;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea",
        "bin", "obj",
        "node_modules", ".npm",
        "packages", ".nuget",
        "__pycache__", ".venv", "venv",
    };

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "directory": {
                    "type": "string",
                    "description": "The directory to list. Can be absolute or relative to the loaded project root, or inside an attached external context folder for the current request. Defaults to the project root if omitted."
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;
    private readonly ExternalContextDirectoryRegistry? _externalContextDirectoryRegistry;

    public ListFilesTool(Func<string?> projectRootProvider, ExternalContextDirectoryRegistry? externalContextDirectoryRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
        _externalContextDirectoryRegistry = externalContextDirectoryRegistry;
    }

    public string Name => "list_files";

    public string Description =>
        "List all files in a directory (recursively). Defaults to the project root if no directory is given. " +
        $"Returns relative paths, up to {MaxFileCount} files. Common noise directories (bin, obj, .git, node_modules, etc.) are excluded. Supports request-scoped external context folders.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? directoryArg = arguments.ValueKind == JsonValueKind.Object &&
                           arguments.TryGetProperty("directory", out var dirElement)
            ? dirElement.GetString()
            : null;

        string resolvedRoot;

        try
        {
            resolvedRoot = string.IsNullOrWhiteSpace(directoryArg)
                ? AgentToolPathResolver.GetProjectRootDirectory(_projectRootProvider)
                : AgentToolPathResolver.ResolvePath(
                    _projectRootProvider,
                    directoryArg,
                    _externalContextDirectoryRegistry?.GetAllowedDirectories());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(resolvedRoot) || !Directory.Exists(resolvedRoot))
        {
            string label = string.IsNullOrWhiteSpace(directoryArg) ? "project root" : $"directory: {directoryArg}";
            return Task.FromResult(ToolCallResult.Fail($"Directory not found: {label}"));
        }

        try
        {
            List<string> files = [];
            CollectFiles(resolvedRoot, resolvedRoot, files, cancellationToken);

            if (files.Count == 0)
            {
                return Task.FromResult(ToolCallResult.Ok("(no files found)"));
            }

            StringBuilder sb = new StringBuilder();
            foreach (var f in files)
            {
                sb.AppendLine(f);
            }

            if (files.Count == MaxFileCount)
            {
                sb.AppendLine($"... (truncated at {MaxFileCount} files)");
            }

            return Task.FromResult(ToolCallResult.Ok(sb.ToString().TrimEnd()));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error: {ex.Message}"));
        }
    }

    private void CollectFiles(string baseDir, string currentDir, List<string> results, CancellationToken cancellationToken)
    {
        if (results.Count >= MaxFileCount)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= MaxFileCount)
                    return;

                results.Add(Path.GetRelativePath(baseDir, file));
            }

            foreach (var dir in Directory.EnumerateDirectories(currentDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= MaxFileCount)
                    return;

                string dirName = Path.GetFileName(dir);
                if (SkippedDirectories.Contains(dirName))
                    continue;

                CollectFiles(baseDir, dir, results, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we cannot access
        }
    }

}
