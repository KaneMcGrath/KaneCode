using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that adds a slide to the active presentation.
/// Each slide navigates the editor to a specific file and line,
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
                    "description": "The path to the file to show. Can be absolute or relative to the project root."
                },
                "line": {
                    "type": "integer",
                    "description": "The line number to navigate to (1-based)."
                },
                "text": {
                    "type": "string",
                    "description": "The explanatory text to display on this slide."
                }
            },
            "required": ["file", "line", "text"]
        }
        """).RootElement.Clone();

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
        "and line, and displays explanatory text in an overlay. Call presentation_new first to create a presentation.";

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

        if (!arguments.TryGetProperty("line", out JsonElement lineElement) ||
            lineElement.ValueKind != JsonValueKind.Number)
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: line"));
        }

        if (!arguments.TryGetProperty("text", out JsonElement textElement) ||
            string.IsNullOrWhiteSpace(textElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: text"));
        }

        string filePath = fileElement.GetString()!.Trim();
        int line = lineElement.GetInt32();
        string text = textElement.GetString()!.Trim();

        string resolvedPath = ResolvePath(filePath);

        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"File not found: {filePath}"));
        }

        _presentationService.AddSlide(resolvedPath, line, text);

        int slideNumber = _presentationService.Slides.Count;
        return Task.FromResult(ToolCallResult.Ok(
            $"Slide {slideNumber} added: {Path.GetFileName(resolvedPath)} line {line}"));
    }

    private string ResolvePath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        string? root = _projectRootProvider();
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
            : Path.Combine(root, filePath);
    }
}
