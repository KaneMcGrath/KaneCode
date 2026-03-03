using System.IO;
using System.Text;
using System.Text.Json;
using KaneCode.Services;
using Microsoft.CodeAnalysis;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that returns Roslyn diagnostics (errors and warnings) for a file.
/// </summary>
internal sealed class GetDiagnosticsTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to the file to get diagnostics for. Can be absolute or relative to the project root."
                }
            },
            "required": ["filePath"]
        }
        """).RootElement.Clone();

    private readonly RoslynWorkspaceService _roslynService;
    private readonly Func<string?> _projectRootProvider;

    public GetDiagnosticsTool(RoslynWorkspaceService roslynService, Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _roslynService = roslynService;
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "get_diagnostics";

    public string Description =>
        "Get Roslyn compiler diagnostics (errors, warnings, and info messages) for a source file. " +
        "Returns each diagnostic with its severity, code, line number, and message. " +
        "Returns 'No diagnostics found.' if the file is clean.";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("filePath", out var filePathElement) ||
            string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            return ToolCallResult.Fail("Missing required parameter: filePath");
        }

        var filePath = filePathElement.GetString()!.Trim();
        var resolvedPath = ResolvePath(filePath);

        // Sync the Roslyn workspace with the latest on-disk content so diagnostics
        // are never stale after agent file edits.
        if (File.Exists(resolvedPath) && RoslynWorkspaceService.IsCSharpFile(resolvedPath))
        {
            try
            {
                var diskContent = File.ReadAllText(resolvedPath);
                await _roslynService.OpenOrUpdateDocumentAsync(resolvedPath, diskContent, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Best-effort; fall through to existing diagnostics path.
            }
        }

        IReadOnlyList<Diagnostic> diagnostics;
        try
        {
            diagnostics = await _roslynService
                .GetDiagnosticsAsync(resolvedPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail($"Failed to get diagnostics: {ex.Message}");
        }

        var relevant = diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .OrderBy(d => d.Location.IsInSource ? d.Location.GetLineSpan().StartLinePosition.Line : int.MaxValue)
            .ThenBy(d => d.Severity)
            .ToList();

        if (relevant.Count == 0)
        {
            return ToolCallResult.Ok("No diagnostics found.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{relevant.Count} diagnostic(s) in '{Path.GetFileName(resolvedPath)}':");
        sb.AppendLine();

        foreach (var diag in relevant)
        {
            var severity = diag.Severity switch
            {
                DiagnosticSeverity.Error   => "ERROR",
                DiagnosticSeverity.Warning => "WARNING",
                DiagnosticSeverity.Info    => "INFO",
                _                          => diag.Severity.ToString().ToUpperInvariant()
            };

            string location;
            if (diag.Location.IsInSource)
            {
                var span = diag.Location.GetLineSpan();
                var line = span.StartLinePosition.Line + 1;       // 1-based
                var col  = span.StartLinePosition.Character + 1;  // 1-based
                location = $"line {line}, col {col}";
            }
            else
            {
                location = "no location";
            }

            sb.AppendLine($"[{severity}] {diag.Id} ({location}): {diag.GetMessage()}");
        }

        var hasErrors = relevant.Any(d => d.Severity == DiagnosticSeverity.Error);
        return hasErrors
            ? ToolCallResult.Fail(sb.ToString().TrimEnd())
            : ToolCallResult.Ok(sb.ToString().TrimEnd());
    }

    private string ResolvePath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        var root = _projectRootProvider();
        if (string.IsNullOrWhiteSpace(root))
        {
            return filePath;
        }

        // Root may point to a .sln or .csproj file — use its directory
        if (File.Exists(root))
        {
            root = Path.GetDirectoryName(root);
        }

        return string.IsNullOrWhiteSpace(root)
            ? filePath
            : Path.GetFullPath(Path.Combine(root, filePath));
    }
}
