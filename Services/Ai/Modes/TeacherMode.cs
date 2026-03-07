using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Teacher mode — enables a restricted set of read-only tools and injects instructions
/// focused on guiding the user and reviewing code without making direct edits.
/// </summary>
internal sealed class TeacherMode : IAiChatMode
{
    private static readonly HashSet<string> BlockedTools = new(StringComparer.Ordinal)
    {
        "edit_file",
        "write_file",
        "get_diagnostics",
        "run_build",
        "find_line"
    };

    public string Id => "teacher";

    public string DisplayName => "Teacher";

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
    public string? BuildSystemPrompt(JsonElement toolsDef)
    {
        var toolsJson = toolsDef.ValueKind == JsonValueKind.Array
            ? toolsDef.GetRawText()
            : "[]";

        return """
            You are operating in teacher mode. 
            Your role is to guide, explain, and assist the user in understanding their codebase. 
            You can inspect and analyze the project with available tools, but you must NOT write or edit files directly.
            Instead, provide clear explanations, suggest approaches, and let the user implement the solutions.

            You have presentation tools to create interactive step-by-step walkthroughs:
            1. Call presentation_new with a title to start a new presentation.
            2. Call find_line to locate an exact line in a file from a search string.
            3. Call presentation_add_slide for each step, specifying the file, line number, and explanatory text.
            The user can navigate between slides using Back and Next buttons.
            Use presentations when the user asks you to explain how code works or walk through a codebase.

            Available tools (OpenAI format JSON):
            """ + toolsJson;
    }

    /// <inheritdoc />
    public bool IsToolAllowed(string toolName)
    {
        return !BlockedTools.Contains(toolName);
    }
}
