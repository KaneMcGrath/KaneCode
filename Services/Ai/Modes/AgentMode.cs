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
        // File system
        "create_directory",
        "delete_directory",
        "delete",
        "edit",
        "list",
        "read",
        "rename_path",
        "search",
        "write",

        // Build & test
        "build",
        "clean",
        "test",

        // Drawing
        "draw_svg",
        "edit_last_svg",

        // Git
        "git_branches",
        "git_checkout",
        "git_commit",
        "git_conflicts",
        "git_create_branch",
        "git_delete_branch",
        "git_diff",
        "git_discard",
        "git_fetch",
        "git_head_file",
        "git_init",
        "git_log",
        "git_pull",
        "git_push",
        "git_resolve_conflict",
        "git_stage",
        "git_status",
        "git_unstage",

        // NuGet
        "nuget_info",
        "nuget_install",
        "nuget_list_installed",
        "nuget_search",
        "nuget_uninstall",
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

            Only invoke git tools when the user explicitly asks for version control operations
            or when the project's documentation indicates they are necessary.
            """;
    }
}
