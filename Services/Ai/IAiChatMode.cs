using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Defines a mode of operation for the AI chat panel.
/// Each mode controls which tools are available and what system prompt
/// is prepended to the conversation. Modes are selectable via a dropdown in the UI.
/// </summary>
internal interface IAiChatMode
{
    /// <summary>
    /// Unique identifier for this mode (e.g. "chat", "agent").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name shown in the mode dropdown (e.g. "Chat", "Agent").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this mode enables tool calling.
    /// When false, no tools are sent in the API request and tool-call
    /// messages are excluded from the context window.
    /// </summary>
    bool ToolsEnabled { get; }

    /// <summary>
    /// Returns the tool definitions to include in the API request, or
    /// <c>default</c> if this mode does not use tools. Called once per send.
    /// </summary>
    /// <param name="registry">The full tool registry.</param>
    JsonElement GetToolDefinitions(AgentToolRegistry registry);

    /// <summary>
    /// Returns an optional system prompt to prepend to the outbound messages.
    /// Return <c>null</c> to skip injecting a mode-specific system prompt.
    /// </summary>
    /// <param name="toolsDef">The serialized tools array (may be <c>default</c>).</param>
    string? BuildSystemPrompt(JsonElement toolsDef);

    /// <summary>
    /// The set of tool names that are allowed in this mode.
    /// <c>null</c> means all tools in the registry are available (unrestricted).
    /// An empty set means no tools are available.
    /// </summary>
    IReadOnlySet<string>? AllowedTools { get; }

    /// <summary>
    /// Returns true if the tool with the given name is allowed to execute in this mode.
    /// Default checks <see cref="AllowedTools"/>: returns true when <c>AllowedTools</c>
    /// is null (unrestricted), or when the tool name is in the set.
    /// </summary>
    bool IsToolAllowed(string toolName) => AllowedTools is null || AllowedTools.Contains(toolName);
}
