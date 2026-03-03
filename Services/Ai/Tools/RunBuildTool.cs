using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that triggers <c>dotnet build</c> on the loaded project or solution
/// and returns the full build output including errors and warnings.
/// </summary>
internal sealed class RunBuildTool : IAgentTool
{
    // Keep the tail of output so the build summary and errors are always included.
    private const int MaxLines = 500;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {},
            "required": []
        }
        """).RootElement.Clone();

    private readonly BuildService _buildService;
    private readonly Func<string?> _projectPathProvider;

    public RunBuildTool(BuildService buildService, Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _buildService = buildService;
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "run_build";

    public string Description =>
        "Trigger a dotnet build of the loaded project or solution. " +
        "Returns the complete build output including compiler errors, warnings, and the final success/failure summary.";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var projectPath = _projectPathProvider();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return ToolCallResult.Fail("No project or solution is currently loaded.");
        }

        var lines = new List<string>();
        var exitCodeTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(string line) => lines.Add(line);
        void OnExited(int code) => exitCodeTcs.TrySetResult(code);

        _buildService.OutputReceived += OnOutput;
        _buildService.ProcessExited += OnExited;

        try
        {
            await _buildService.BuildAsync(projectPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildService.OutputReceived -= OnOutput;
            _buildService.ProcessExited -= OnExited;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Build was cancelled.");
        }

        // ProcessExited fires inside BuildAsync before it returns, so the TCS is always set here.
        var exitCode = exitCodeTcs.Task.IsCompleted
            ? exitCodeTcs.Task.Result
            : await exitCodeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        var output = FormatOutput(lines);
        var succeeded = exitCode == 0;
        var summary = succeeded
            ? $"Build succeeded (exit code 0)."
            : $"Build failed (exit code {exitCode}).";

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
