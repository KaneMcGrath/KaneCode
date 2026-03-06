using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Agent mode — enables all registered tools and injects agent instructions
/// into the system prompt so the model uses tools to complete tasks.
/// </summary>
internal sealed class AgentMode : IAiChatMode
{
    private static readonly HashSet<string> BlockedTools = new(StringComparer.Ordinal)
    {
        "presentation_new",
        "find_line",
        "presentation_add_slide"
    };

    public string Id => "agent";

    public string DisplayName => "Agent";

    public bool ToolsEnabled => true;

    /// <inheritdoc />
    public JsonElement GetToolDefinitions(AgentToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!registry.HasTools)
        {
            return default;
        }

        var allowedTools = registry.Tools
            .Select(t => t.Name)
            .Where(name => !BlockedTools.Contains(name));

        return registry.SerializeToolDefinitions(allowedTools);
    }

    /// <inheritdoc />
    public bool IsToolAllowed(string toolName)
    {
        return !BlockedTools.Contains(toolName);
    }

    /// <inheritdoc />
    public string? BuildSystemPrompt(JsonElement toolsDef)
    {
        var toolsJson = toolsDef.ValueKind == JsonValueKind.Array
            ? toolsDef.GetRawText()
            : "[]";

        return """
            You are operating in agent mode.
            Use available tools whenever they are needed to inspect files, gather diagnostics, and make precise edits.
            Before calling a tool, think briefly about why the call is needed.
            After receiving a tool result, continue until the request is completed.

            Available tools (OpenAI format JSON):
            """ + toolsJson;
    }
}
