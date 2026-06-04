using KaneCode.Models;

namespace KaneCode.Services.Ai;

/// <summary>
/// Represents a persisted AI chat conversation and its attached context.
/// </summary>
internal sealed class AiConversation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "New conversation";

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool ProjectContextInjected { get; set; }

    /// <summary>
    /// Per-conversation set of enabled tool names. When null, all tools
    /// allowed by the active mode are available. When non-null, only the
    /// listed tool names are enabled for this conversation.
    /// </summary>
    public HashSet<string>? EnabledTools { get; set; }

    /// <summary>
    /// Custom system prompt override. When set, this is used instead of
    /// the active mode's <see cref="IAiChatMode.BuildSystemPrompt"/>.
    /// Cleared when the user switches to a preset mode.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// The ID of the last non-Custom mode selected, so we can detect
    /// when the user re-selects a preset to reset tools and prompt.
    /// </summary>
    public string? BaseModeId { get; set; }

    public List<AiChatMessage> Messages { get; } = [];

    public List<AiChatReference> References { get; } = [];
}

/// <summary>
/// Stores all persisted conversations for a project and tracks the active one.
/// </summary>
internal sealed class AiConversationState
{
    public string? ActiveConversationId { get; set; }

    public List<AiConversation> Conversations { get; } = [];
}
