using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Agent mode — enables all registered tools and injects agent instructions
/// into the system prompt so the model uses tools to complete tasks.
/// </summary>
internal sealed class AgentMode : IAiChatMode
{
    private static readonly HashSet<string> AllowedToolsSet = new(StringComparer.Ordinal)
    {
        "create_directory",
        "delete_directory",
        "delete_file",
        "edit_file",
        "list_files",
        "read_file",
        "rename_path",
        "run_build",
        "run_test",
        "search_files",
        "write_file",
    };

    public string Id => "agent";

    public string DisplayName => "Agent";

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
    public string? BuildSystemPrompt(JsonElement toolsDef)
    {
        return """
            You are operating in agent mode.
            Use available tools whenever they are needed to inspect files, gather diagnostics, and make precise edits.
            Before calling a tool, think briefly about why the call is needed.
            After receiving a tool result, continue until the request is completed.
            """;
    }
}
