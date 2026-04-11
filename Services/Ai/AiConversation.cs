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
