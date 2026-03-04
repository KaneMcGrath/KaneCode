using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Plain chat mode — no tools, no agent instructions.
/// The model behaves as a standard conversational assistant.
/// </summary>
internal sealed class ChatMode : IAiChatMode
{
    public string Id => "chat";

    public string DisplayName => "Chat";

    public bool ToolsEnabled => false;

    /// <inheritdoc />
    public JsonElement GetToolDefinitions(AgentToolRegistry registry, IReadOnlyCollection<string>? enabledToolNames) => default;

    /// <inheritdoc />
    public string? BuildSystemPrompt(JsonElement toolsDef) => null;
}
