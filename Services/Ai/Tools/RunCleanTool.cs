using System.IO;
using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that triggers <c>dotnet clean</c> on the loaded project or solution
/// and returns the full output.
/// </summary>
internal sealed class RunCleanTool : IAgentTool
{
    private const int MaxLines = 500;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "project": {
                    "type": "string",
                    "description": "Optional path to the project or solution to clean. If omitted, the currently loaded project/solution is used. Supports relative paths."
                },
                "configuration": {
                    "type": "string",
                    "description": "Optional build configuration to clean: Debug or Release. If omitted, all configurations are cleaned.",
                    "enum": ["Debug", "Release"]
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly BuildService _buildService;
    private readonly Func<string?> _projectPathProvider;

    public RunCleanTool(BuildService buildService, Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _buildService = buildService;
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "clean";

    public string Category => "Dotnet";

    public string Description =>
        "Run 'dotnet clean' on a project or solution to delete build artifacts (bin/obj folders). " +
        "Use this before a fresh build to ensure no stale binaries interfere, " +
        "or to reclaim disk space. Optionally scoped to a specific configuration.";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? projectPath = null;
        if (arguments.TryGetProperty("project", out JsonElement projectElement) &&
            projectElement.ValueKind == JsonValueKind.String)
        {
            string? raw = projectElement.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                projectPath = raw.Trim();
            }
        }

        projectPath ??= _projectPathProvider();

        if (!string.IsNullOrWhiteSpace(projectPath) && !Path.IsPathRooted(projectPath))
        {
            string rootDir = AgentToolPathResolver.GetProjectRootDirectory(_projectPathProvider);
            projectPath = Path.GetFullPath(Path.Combine(rootDir, projectPath));
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return ToolCallResult.Fail("No project or solution is currently loaded, and no project path was provided.");
        }

        string? configuration = null;
        if (arguments.TryGetProperty("configuration", out JsonElement configElement) &&
            configElement.ValueKind == JsonValueKind.String)
        {
            configuration = configElement.GetString()?.Trim();
        }

        var lines = new List<string>();
        var exitCodeTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(string line) => lines.Add(line);
        void OnExited(int code) => exitCodeTcs.TrySetResult(code);

        _buildService.OutputReceived += OnOutput;
        _buildService.ProcessExited += OnExited;

        try
        {
            await _buildService.CleanAsync(projectPath, configuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildService.OutputReceived -= OnOutput;
            _buildService.ProcessExited -= OnExited;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Clean was cancelled.");
        }

        var exitCode = exitCodeTcs.Task.IsCompleted
            ? exitCodeTcs.Task.Result
            : await exitCodeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        var output = FormatOutput(lines);
        var succeeded = exitCode == 0;
        var summary = succeeded
            ? $"Clean succeeded (exit code 0)."
            : $"Clean failed (exit code {exitCode}).";

        var result = $"{summary}\n\n{output}";
        return succeeded ? ToolCallResult.Ok(result) : ToolCallResult.Fail(result);
    }

    private static string FormatOutput(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return "(no output)";
        }

        var sb = new StringBuilder();

        if (lines.Count > MaxLines)
        {
            var omitted = lines.Count - MaxLines;
            sb.AppendLine($"... ({omitted} lines omitted from the start)");
            foreach (var line in lines.Skip(omitted))
            {
                sb.AppendLine(line);
            }
        }
        else
        {
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
