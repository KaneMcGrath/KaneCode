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
                    "description": "The directory to list. Can be absolute or relative to the project root. Defaults to the project root if omitted."
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public ListFilesTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "list_files";

    public string Description =>
        "List all files in a directory (recursively). Defaults to the project root if no directory is given. " +
        $"Returns relative paths, up to {MaxFileCount} files. Common noise directories (bin, obj, .git, node_modules, etc.) are excluded.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var directoryArg = arguments.ValueKind == JsonValueKind.Object &&
                           arguments.TryGetProperty("directory", out var dirElement)
            ? dirElement.GetString()
            : null;

        var resolvedRoot = ResolveRoot(directoryArg);

        if (string.IsNullOrWhiteSpace(resolvedRoot) || !Directory.Exists(resolvedRoot))
        {
            var label = string.IsNullOrWhiteSpace(directoryArg) ? "project root" : $"directory: {directoryArg}";
            return Task.FromResult(ToolCallResult.Fail($"Directory not found: {label}"));
        }

        try
        {
            var files = new List<string>();
            CollectFiles(resolvedRoot, resolvedRoot, files, cancellationToken);

            if (files.Count == 0)
            {
                return Task.FromResult(ToolCallResult.Ok("(no files found)"));
            }

            var sb = new StringBuilder();
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

                var dirName = Path.GetFileName(dir);
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

    private string? ResolveRoot(string? directory)
    {
        var projectRoot = _projectRootProvider();

        // Resolve the project root: if it points to a file (.sln/.csproj), use its directory
        if (!string.IsNullOrWhiteSpace(projectRoot) && File.Exists(projectRoot))
        {
            projectRoot = Path.GetDirectoryName(projectRoot);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return projectRoot;
        }

        if (Path.IsPathRooted(directory))
        {
            return directory;
        }

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return directory;
        }

        return Path.GetFullPath(Path.Combine(projectRoot, directory));
    }
}
