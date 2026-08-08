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
            "properties": {
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

    public RunBuildTool(BuildService buildService, Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _buildService = buildService;
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "build";

    public string Category => "Dotnet";

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

        string? configuration = null;
        if (arguments.TryGetProperty("configuration", out JsonElement configElement) &&
            configElement.ValueKind == JsonValueKind.String)
        {
            configuration = configElement.GetString()?.Trim();
        }

        var lines = new List<string>();

        // Capture output through the scoped per-invocation callback rather than the
        // global OutputReceived/ProcessExited events. Those events also carry events
        // raised by the previous process that this call cancels (e.g. a stale
        // "Build/Run cancelled." line and exit code -1 from a superseded build), which
        // would otherwise be misattributed to this build and report success as failure.
        int exitCode;
        try
        {
            exitCode = await _buildService.BuildAsync(
                projectPath,
                configuration: configuration,
                cancellationToken: cancellationToken,
                onOutput: lines.Add).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Build was cancelled.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ToolCallResult.Fail("Build was cancelled.");
        }

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
