using System.IO;
using System.Text.Json;
using Svg;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that renders an SVG image inline in the chat for visual feedback.
/// This tool is for communicating visual ideas to the user — diagrams, charts,
/// illustrations, and other graphical concepts that are best expressed visually.
/// Does not write files; use <c>write</c> to persist the SVG to disk.
/// </summary>
internal sealed class DrawSvgTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "content": {
                    "type": "string",
                    "description": "The full SVG markup content. Must be valid SVG XML."
                }
            },
            "required": ["content"]
        }
        """).RootElement.Clone();

    public DrawSvgTool()
    {
    }

    public string Name => "draw_svg";

    public string Category => "Drawing";

    public string Description =>
        "Renders an SVG image inline in the chat for visual feedback. " +
        "Use this tool to communicate visual ideas to the user — diagrams, " +
        "charts, illustrations, flowcharts, architecture diagrams, and other " +
        "graphical concepts. The SVG is displayed directly in the conversation. " +
        "Does not require a loaded project. " +
        "If the SVG also needs to be saved to disk, use the write tool separately.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => false;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("content", out var contentElement))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: content"));
        }

        string content = contentElement.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(ToolCallResult.Fail("SVG content cannot be empty."));
        }

        // Validate that the SVG is well-formed by parsing it
        try
        {
            using var svgStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            SvgDocument.Open<SvgDocument>(svgStream);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolCallResult.Fail(
                $"Invalid SVG content: {ex.Message}"));
        }

        return Task.FromResult(ToolCallResult.OkWithSvg(
            "SVG rendered successfully.", content));
    }
}
