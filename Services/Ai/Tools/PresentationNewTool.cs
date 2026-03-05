using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that creates a new interactive presentation session.
/// Clears any existing presentation and starts fresh with the given title.
/// </summary>
internal sealed class PresentationNewTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "title": {
                    "type": "string",
                    "description": "The title of the presentation."
                }
            },
            "required": ["title"]
        }
        """).RootElement.Clone();

    private readonly PresentationService _presentationService;

    public PresentationNewTool(PresentationService presentationService)
    {
        ArgumentNullException.ThrowIfNull(presentationService);
        _presentationService = presentationService;
    }

    public string Name => "presentation_new";

    public string Description =>
        "Creates a new interactive presentation to explain topics in the codebase. " +
        "Call this before adding slides. Any existing presentation is replaced.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("title", out JsonElement titleElement) ||
            string.IsNullOrWhiteSpace(titleElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: title"));
        }

        string title = titleElement.GetString()!.Trim();
        _presentationService.NewPresentation(title);

        return Task.FromResult(ToolCallResult.Ok($"Presentation created: \"{title}\". Add slides with presentation_add_slide."));
    }
}
