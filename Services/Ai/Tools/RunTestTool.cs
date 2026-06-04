using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that triggers <c>dotnet test</c> on the loaded project or solution
/// and returns the full test output including results, errors, and summary.
/// Output is also streamed to the Build Output panel in the IDE.
/// </summary>
internal sealed class RunTestTool : IAgentTool
{
    // Keep the tail of output so the test summary and failures are always included.
    private const int MaxLines = 500;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "project": {
                    "type": "string",
                    "description": "Optional path to the test project or solution. If omitted, the currently loaded project/solution is used."
                },
                "filter": {
                    "type": "string",
                    "description": "Optional test filter expression to run a subset of tests, e.g. 'FullyQualifiedName~MyTest' or 'Category=Unit'. See https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-filter"
                },
                "configuration": {
                    "type": "string",
                    "description": "Optional build configuration: Debug or Release. Defaults to Debug if not specified.",
                    "enum": ["Debug", "Release"]
                },
                "framework": {
                    "type": "string",
                    "description": "Optional target framework moniker, e.g. 'net8.0'. Only needed if the project targets multiple frameworks."
                },
                "verbosity": {
                    "type": "string",
                    "description": "Optional verbosity level for test output.",
                    "enum": ["quiet", "minimal", "normal", "detailed", "diagnostic"]
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly BuildService _buildService;
    private readonly Func<string?> _projectPathProvider;

    public RunTestTool(BuildService buildService, Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _buildService = buildService;
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "run_test";

    public string Category => "Dotnet";

    public string Description =>
        "Run dotnet test on a test project or solution. " +
        "Output is displayed in the Build Output panel in the IDE. " +
        "Returns the complete test output including results, failures, and a pass/fail summary. " +
        "Use this to verify code changes pass existing tests or to run specific tests by filter.";

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

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return ToolCallResult.Fail("No project or solution is currently loaded, and no project path was provided.");
        }

        string? filter = null;
        if (arguments.TryGetProperty("filter", out JsonElement filterElement) &&
            filterElement.ValueKind == JsonValueKind.String)
        {
            filter = filterElement.GetString()?.Trim();
        }

        string? configuration = null;
        if (arguments.TryGetProperty("configuration", out JsonElement configElement) &&
            configElement.ValueKind == JsonValueKind.String)
        {
            configuration = configElement.GetString()?.Trim();
        }

        string? framework = null;
        if (arguments.TryGetProperty("framework", out JsonElement frameworkElement) &&
            frameworkElement.ValueKind == JsonValueKind.String)
        {
            framework = frameworkElement.GetString()?.Trim();
        }

        string? verbosity = null;
        if (arguments.TryGetProperty("verbosity", out JsonElement verbosityElement) &&
            verbosityElement.ValueKind == JsonValueKind.String)
        {
            verbosity = verbosityElement.GetString()?.Trim();
        }

        var lines = new List<string>();
        var exitCodeTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(string line) => lines.Add(line);
        void OnExited(int code) => exitCodeTcs.TrySetResult(code);

        _buildService.OutputReceived += OnOutput;
        _buildService.ProcessExited += OnExited;

        try
        {
            await _buildService.TestAsync(projectPath, filter, configuration, framework, verbosity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildService.OutputReceived -= OnOutput;
            _buildService.ProcessExited -= OnExited;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Test run was cancelled.");
        }

        var exitCode = exitCodeTcs.Task.IsCompleted
            ? exitCodeTcs.Task.Result
            : await exitCodeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        var output = FormatOutput(lines);
        var succeeded = exitCode == 0;
        var summary = succeeded
            ? "Tests passed (exit code 0)."
            : $"Tests failed (exit code {exitCode}).";

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
