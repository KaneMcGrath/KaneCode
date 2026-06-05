using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// Application mode — the default mode when KaneCode launches and no project is loaded.
/// Provides tools to load projects, create new projects, browse the file system, and
/// answer questions. Recent projects are listed in the system prompt so the AI can
/// use <c>load_project</c> to open them by path.
/// When a project is loaded, the IDE automatically switches to agent mode.
/// </summary>
internal sealed class ApplicationMode : IAiChatMode
{
    private static readonly HashSet<string> AllowedToolsSet = new(StringComparer.Ordinal)
    {
        "load_project",
        "new_project",
        "read",
        "list",
        "search",
    };

    private readonly Func<IReadOnlyList<Models.RecentProjectItem>>? _recentProjectsProvider;
    private readonly Func<string>? _defaultProjectFolderProvider;

    public ApplicationMode(
        Func<IReadOnlyList<Models.RecentProjectItem>>? recentProjectsProvider = null,
        Func<string>? defaultProjectFolderProvider = null)
    {
        _recentProjectsProvider = recentProjectsProvider;
        _defaultProjectFolderProvider = defaultProjectFolderProvider;
    }

    public string Id => "application";

    public string DisplayName => "Application";

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
        string recentProjectsSection = BuildRecentProjectsSection();
        string defaultLocationSection = BuildDefaultLocationSection();

        return $"""
            You are operating in application mode.
            This is the default mode when KaneCode launches, before any project is loaded.

            Your role is to help the user manage their development environment:
            - Load a project (solution .sln, project .csproj, or folder) by its file path.
            - Create a new .NET project from a template.
            - Browse, read, list, and search files in the default project location.
            - Answer general questions about KaneCode, .NET development, and programming.
            {recentProjectsSection}
            {defaultLocationSection}
            When a project is successfully loaded, the IDE will automatically switch to agent mode
            so you can inspect files, gather diagnostics, and make precise edits.

            Use the `load_project` tool with a full path to open any project.
            Use the `new_project` tool to create a new .NET project from a template.
            Use the `list` tool to browse files in a directory.
            Use the `search` tool to search file contents.
            Use the `read` tool to read file contents.

            Before calling a tool, think briefly about why the call is needed.
            After receiving a tool result, continue until the request is completed.
            Be helpful and conversational when the user asks general questions.
            """;
    }

    private string BuildRecentProjectsSection()
    {
        if (_recentProjectsProvider is null)
        {
            return string.Empty;
        }

        var entries = _recentProjectsProvider();
        if (entries is null || entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Recent projects (use load_project with the full path to open):");

        foreach (var entry in entries.Take(15))
        {
            string icon = entry.ItemType switch
            {
                Models.RecentItemType.Solution => "\U0001F5C2\uFE0F",  // 🗂️
                Models.RecentItemType.Project => "\U0001F4E6",         // 📦
                Models.RecentItemType.Folder => "\U0001F4C1",          // 📁
                _ => "\U0001F4C4"                                       // 📄
            };

            sb.AppendLine($"  {icon} {entry.DisplayName} ({entry.ItemType})");
            sb.AppendLine($"    Path: {entry.FullPath}");
        }

        return sb.ToString();
    }

    private string BuildDefaultLocationSection()
    {
        if (_defaultProjectFolderProvider is null)
        {
            return string.Empty;
        }

        string defaultFolder = _defaultProjectFolderProvider();
        if (string.IsNullOrWhiteSpace(defaultFolder))
        {
            return string.Empty;
        }

        return $"""
            
            The user's default project folder is: {defaultFolder}
            When no project is loaded, the `list`, `search`, and `read` tools operate relative to this directory.
            """;
    }
}
