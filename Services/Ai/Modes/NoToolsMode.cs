using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// No Tools mode — disables all tool calling.
/// The model behaves as a plain conversational assistant with no
/// ability to inspect files, run git commands, or make modifications.
/// </summary>
internal sealed class NoToolsMode : IAiChatMode
{
    private static readonly HashSet<string> EmptyTools = [];

    public string Id => "no_tools";

    public string DisplayName => "No Tools";

    public bool ToolsEnabled => false;

    /// <inheritdoc />
    public IReadOnlySet<string>? AllowedTools => EmptyTools;

    /// <inheritdoc />
    public JsonElement GetToolDefinitions(AgentToolRegistry registry) => default;

    /// <inheritdoc />
    public string? BuildSystemPrompt(JsonElement toolsDef)
    {
        return """
            You are a helpful conversational assistant.
            Answer the user's questions clearly and concisely.
            """;
    }
}
