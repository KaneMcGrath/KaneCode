using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Custom mode — auto-selected when the user manually changes tools or
/// the system prompt away from a preset mode's defaults.
/// Tools and system prompt are stored in the conversation itself.
/// </summary>
internal sealed class CustomMode : IAiChatMode
{
    public string Id => "custom";

    public string DisplayName => "Custom";

    public bool ToolsEnabled => true;

    /// <inheritdoc />
    public JsonElement GetToolDefinitions(AgentToolRegistry registry) => default;

    /// <inheritdoc />
    public string? BuildSystemPrompt(JsonElement toolsDef) => null;

    /// <inheritdoc />
    public bool IsToolAllowed(string toolName) => true;
}
