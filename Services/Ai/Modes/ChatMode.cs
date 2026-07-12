using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Chat mode — enables drawing tools (SVG) for visual communication.
/// The model behaves as a standard conversational assistant but can
/// render diagrams, charts, and other graphical concepts inline.
/// </summary>
internal sealed class ChatMode : IAiChatMode
{
    private static readonly HashSet<string> AllowedToolsSet = new(StringComparer.Ordinal)
    {
        "draw_svg",
        "edit_last_svg",
    };

    public string Id => "chat";

    public string DisplayName => "Chat";

    public bool ToolsEnabled => true;

    /// <inheritdoc />
    public IReadOnlySet<string>? AllowedTools => AllowedToolsSet;

    /// <inheritdoc />
    public JsonElement GetToolDefinitions(AgentToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!registry.HasTools)
        {
            return default;
        }

        return registry.SerializeToolDefinitions(AllowedToolsSet);
    }

    /// <inheritdoc />
    public string? BuildSystemPrompt(JsonElement toolsDef) => null;
}
