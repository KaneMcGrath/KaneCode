using System.IO;
using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that runs <c>dotnet run</c> on a project and captures its output.
/// Supports optional program arguments, build configuration, and a timeout
/// after which the process is killed. Designed for agents to assess runtime
/// errors, exceptions, and program behavior.
/// Output is also streamed to the Build Output panel in the IDE.
/// </summary>
internal sealed class RunDotnetTool : IAgentTool
{
    private const int MaxLines = 500;
    private const int DefaultTimeoutSeconds = 30;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "project": {
                    "type": "string",
                    "description": "Optional path to the project or solution to run. If omitted, the currently loaded project/solution is used. Supports relative paths."
                },
                "arguments": {
                    "type": "string",
                    "description": "Optional command-line arguments to pass to the program being run. These are passed after '--' to dotnet run."
                },
                "timeout": {
                    "type": "number",
                    "description": "Optional timeout in seconds. After this many seconds the program is force-killed. Defaults to 30. Set to 0 for no timeout (runs until the program exits on its own)."
                },
                "configuration": {
                    "type": "string",
                    "description": "Optional build configuration: Debug or Release. Defaults to Debug if not specified.",
                    "enum": ["Debug", "Release"]
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly BuildService _buildService;
    private readonly Func<string?> _projectPathProvider;

    public RunDotnetTool(BuildService buildService, Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _buildService = buildService;
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "run";

    public string Category => "Dotnet";

    public string Description =>
        "Run a .NET project or solution using 'dotnet run' and capture all stdout/stderr output. " +
        "Use this to test if a program runs correctly, check for runtime errors or exceptions, " +
        "observe program behavior, or verify that a program produces expected output. " +
        "Supports passing command-line arguments to the program and setting a timeout " +
        "so the agent does not hang on long-running or interactive programs. " +
        "The output is also streamed to the Build Output panel in the IDE.";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        // ── Resolve project path ──────────────────────────────────
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

        // Resolve relative paths against the loaded solution/project directory
        if (!string.IsNullOrWhiteSpace(projectPath) && !Path.IsPathRooted(projectPath))
        {
            string rootDir = AgentToolPathResolver.GetProjectRootDirectory(_projectPathProvider);
            projectPath = Path.GetFullPath(Path.Combine(rootDir, projectPath));
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return ToolCallResult.Fail("No project or solution is currently loaded, and no project path was provided.");
        }

        // ── Parse arguments ───────────────────────────────────────
        string? programArgs = null;
        if (arguments.TryGetProperty("arguments", out JsonElement argsElement) &&
            argsElement.ValueKind == JsonValueKind.String)
        {
            programArgs = argsElement.GetString()?.Trim();
        }

        int? timeoutSeconds = null;
        if (arguments.TryGetProperty("timeout", out JsonElement timeoutElement) &&
            (timeoutElement.ValueKind == JsonValueKind.Number || timeoutElement.ValueKind == JsonValueKind.String))
        {
            if (timeoutElement.ValueKind == JsonValueKind.Number)
            {
                int val = timeoutElement.GetInt32();
                if (val > 0)
                {
                    timeoutSeconds = val;
                }
            }
            else if (int.TryParse(timeoutElement.GetString(), out int parsed) && parsed > 0)
            {
                timeoutSeconds = parsed;
            }
        }

        // Default timeout if none specified
        timeoutSeconds ??= DefaultTimeoutSeconds;

        string? configuration = null;
        if (arguments.TryGetProperty("configuration", out JsonElement configElement) &&
            configElement.ValueKind == JsonValueKind.String)
        {
            configuration = configElement.GetString()?.Trim();
        }

        // ── Build linked cancellation token with timeout ──────────
        CancellationTokenSource? timeoutCts = null;
        CancellationToken effectiveToken = cancellationToken;

        if (timeoutSeconds.HasValue)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.Value));
            effectiveToken = timeoutCts.Token;
        }

        // ── Capture output ────────────────────────────────────────
        var lines = new List<string>();
        var exitCodeTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(string line) => lines.Add(line);
        void OnExited(int code) => exitCodeTcs.TrySetResult(code);

        _buildService.OutputReceived += OnOutput;
        _buildService.ProcessExited += OnExited;

        bool wasTimedOut = false;

        try
        {
            await _buildService.RunProjectAsync(projectPath, programArgs, configuration, effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            // Timeout triggered, not user cancellation
            wasTimedOut = true;
            lines.Add($"(process timed out after {timeoutSeconds} seconds and was killed)");
            exitCodeTcs.TrySetResult(-1);
        }
        finally
        {
            _buildService.OutputReceived -= OnOutput;
            _buildService.ProcessExited -= OnExited;
            timeoutCts?.Dispose();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Run was cancelled by the user.");
        }

        var exitCode = exitCodeTcs.Task.IsCompleted
            ? exitCodeTcs.Task.Result
            : await exitCodeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        var output = FormatOutput(lines);

        string summary;
        if (wasTimedOut)
        {
            summary = $"Program was killed after {timeoutSeconds} second timeout (exit code {exitCode}).";
        }
        else if (exitCode == 0)
        {
            summary = $"Program exited successfully (exit code 0).";
        }
        else
        {
            summary = $"Program exited with code {exitCode}.";
        }

        var result = $"{summary}\n\n{output}";
        return exitCode == 0 ? ToolCallResult.Ok(result) : ToolCallResult.Fail(result);
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
