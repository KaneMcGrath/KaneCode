using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that adds a slide to the active presentation.
/// Each slide navigates the editor to a specific file and a located line,
/// displaying explanatory text in an overlay next to the code.
/// </summary>
internal sealed class PresentationAddSlideTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "file": {
                    "type": "string",
                    "description": "The path to the file to show. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
                },
                "searchString": {
                    "type": "string",
                    "description": "The text to locate in the file. The slide will navigate to the first matching line, or line 0 if not found."
                },
                "text": {
                    "type": "string",
                    "description": "The explanatory text to display on this slide."
                }
            },
            "required": ["file", "searchString", "text"]
        }
        """).RootElement.Clone();

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages"
    };

    private readonly PresentationService _presentationService;
    private readonly Func<string?> _projectRootProvider;

    public PresentationAddSlideTool(PresentationService presentationService, Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(presentationService);
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _presentationService = presentationService;
        _projectRootProvider = projectRootProvider;
    }

    public string Name => "presentation_add_slide";

    public string Description =>
        "Adds a slide to the active presentation. The slide navigates the editor to the specified file " +
        "and the first line containing the provided search text, and displays explanatory text in an overlay. " +
        "Call presentation_new first to create a presentation.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!_presentationService.IsActive)
        {
            return Task.FromResult(ToolCallResult.Fail(
                "No active presentation. Call presentation_new first to create one."));
        }

        if (!arguments.TryGetProperty("file", out JsonElement fileElement) ||
            string.IsNullOrWhiteSpace(fileElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: file"));
        }

        if (!arguments.TryGetProperty("searchString", out JsonElement searchElement) ||
            string.IsNullOrWhiteSpace(searchElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: searchString"));
        }

        if (!arguments.TryGetProperty("text", out JsonElement textElement) ||
            string.IsNullOrWhiteSpace(textElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: text"));
        }

        string filePath = fileElement.GetString()!.Trim();
        string searchString = searchElement.GetString()!.Trim();
        string text = textElement.GetString()!.Trim();

        string resolvedPath;

        try
        {
            resolvedPath = AgentToolPathResolver.ResolveFilePathOrFileName(_projectRootProvider, filePath, SkippedDirectories);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"File not found: {filePath}"));
        }

        int line;
        try
        {
            line = FindFirstMatchingLine(resolvedPath, searchString);
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error reading file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }

        _presentationService.AddSlide(resolvedPath, line, text);

        int slideNumber = _presentationService.Slides.Count;
        return Task.FromResult(ToolCallResult.Ok(
            line > 0
                ? $"Slide {slideNumber} added: {Path.GetFileName(resolvedPath)} line {line}"
                : $"Slide {slideNumber} added: {Path.GetFileName(resolvedPath)} line 0 (search text not found)"));
    }

    private static int FindFirstMatchingLine(string filePath, string searchString)
    {
        int lineNumber = 0;
        foreach (string line in File.ReadLines(filePath))
        {
            lineNumber++;
            if (line.Contains(searchString, StringComparison.OrdinalIgnoreCase))
            {
                return lineNumber;
            }
        }

        return 0;
    }

}
