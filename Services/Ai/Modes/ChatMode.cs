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
        // Drawing — visual communication only, no side effects
        "draw_svg",
        "edit_last_svg",

        // Diagnostics and code intelligence — read-only
        "get_diagnostics",

        // Git inspection — read-only queries
        "git_branches",
        "git_conflicts",
        "git_diff",
        "git_head_file",
        "git_log",
        "git_status",

        // File system browsing — read-only
        "list",
        "read",
        "search",

        // NuGet browsing — read-only queries
        "nuget_info",
        "nuget_list_installed",
        "nuget_search",
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
