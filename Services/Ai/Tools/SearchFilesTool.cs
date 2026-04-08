using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that searches file contents with a plain-text or regex query.
/// Returns matching file paths with line numbers and line snippets.
/// </summary>
internal sealed class SearchFilesTool : IAgentTool
{
    private const int MaxMatches = 200;
    private const int MaxFileSizeBytes = 500 * 1024; // 500 KB — skip binary/huge files
    private const int MaxSnippetLength = 120;

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
                "query": {
                    "type": "string",
                    "description": "The text or regex pattern to search for."
                },
                "directory": {
                    "type": "string",
                    "description": "Directory to search in. Can be absolute or relative to the loaded project root, but must stay inside the loaded project. Defaults to the project root if omitted."
                },
                "isRegex": {
                    "type": "boolean",
                    "description": "If true, treats the query as a .NET regular expression. Defaults to false."
                }
            },
            "required": ["query"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;

    public SearchFilesTool(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "search_files";

    public string Description =>
        "Search file contents recursively using a plain-text or regex query. " +
        $"Returns up to {MaxMatches} matches as 'file:line: snippet'. " +
        "Common noise directories (bin, obj, .git, node_modules, etc.) are excluded.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("query", out var queryElement) ||
            string.IsNullOrEmpty(queryElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: query"));
        }

        string query = queryElement.GetString()!;

        string? directoryArg = arguments.TryGetProperty("directory", out var dirElement)
            ? dirElement.GetString()
            : null;

        bool useRegex = arguments.TryGetProperty("isRegex", out var regexElement) &&
                       regexElement.ValueKind == JsonValueKind.True;

        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                regex = new Regex(query, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolCallResult.Fail($"Invalid regex pattern: {ex.Message}"));
            }
        }

        string resolvedRoot;

        try
        {
            resolvedRoot = string.IsNullOrWhiteSpace(directoryArg)
                ? AgentToolPathResolver.GetProjectRootDirectory(_projectRootProvider)
                : AgentToolPathResolver.ResolvePath(_projectRootProvider, directoryArg);
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
            List<string> matches = [];
            SearchDirectory(resolvedRoot, resolvedRoot, query, regex, matches, cancellationToken);

            if (matches.Count == 0)
            {
                return Task.FromResult(ToolCallResult.Ok("No matches found."));
            }

            StringBuilder sb = new StringBuilder();
            foreach (var match in matches)
            {
                sb.AppendLine(match);
            }

            if (matches.Count == MaxMatches)
            {
                sb.AppendLine($"... (truncated at {MaxMatches} matches)");
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

    private void SearchDirectory(
        string baseDir,
        string currentDir,
        string query,
        Regex? regex,
        List<string> results,
        CancellationToken cancellationToken)
    {
        if (results.Count >= MaxMatches)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= MaxMatches)
                    return;

                cancellationToken.ThrowIfCancellationRequested();
                SearchFile(baseDir, file, query, regex, results);
            }

            foreach (var dir in Directory.EnumerateDirectories(currentDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= MaxMatches)
                    return;

                string dirName = Path.GetFileName(dir);
                if (SkippedDirectories.Contains(dirName))
                    continue;

                SearchDirectory(baseDir, dir, query, regex, results, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we cannot access
        }
    }

    private void SearchFile(string baseDir, string filePath, string query, Regex? regex, List<string> results)
    {
        try
        {
            FileInfo info = new FileInfo(filePath);
            if (info.Length > MaxFileSizeBytes)
                return;

            string relativePath = Path.GetRelativePath(baseDir, filePath);
            int lineNumber = 0;

            foreach (var line in File.ReadLines(filePath))
            {
                lineNumber++;

                if (results.Count >= MaxMatches)
                    return;

                bool matched = regex is not null
                    ? regex.IsMatch(line)
                    : line.Contains(query, StringComparison.OrdinalIgnoreCase);

                if (!matched)
                    continue;

                string snippet = line.Trim();
                if (snippet.Length > MaxSnippetLength)
                    snippet = string.Concat(snippet.AsSpan(0, MaxSnippetLength), "…");

                results.Add($"{relativePath}:{lineNumber}: {snippet}");
            }
        }
        catch (IOException)
        {
            // Skip files we cannot read
        }
        catch (UnauthorizedAccessException)
        {
            // Skip files we cannot access
        }
        catch (RegexMatchTimeoutException)
        {
            // Skip files where the regex times out
        }
    }

}
