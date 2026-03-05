using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Teacher mode — enables a restricted set of read-only tools and injects instructions
/// focused on guiding the user and reviewing code without making direct edits.
/// </summary>
internal sealed class TeacherMode : IAiChatMode
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
    {
        "list_files",
        "read_file",
        "search_files"
    };

    public string Id => "teacher";

    public string DisplayName => "Teacher";

    public bool ToolsEnabled => true;

    /// <inheritdoc />
    public JsonElement GetToolDefinitions(AgentToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.HasTools
            ? registry.SerializeToolDefinitions(AllowedTools)
            : default;
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
            You can read and search files to gather context, but you must NOT write or edit code directly for the user.
            Instead, provide clear explanations, suggest approaches, and let the user implement the solutions.

            Available reading tools (OpenAI format JSON):
            """ + toolsJson;
    }

    /// <inheritdoc />
    public bool IsToolAllowed(string toolName)
    {
        return AllowedTools.Contains(toolName);
    }
}
