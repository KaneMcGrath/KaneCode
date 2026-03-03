namespace KaneCode.Services.Ai;

/// <summary>
/// Role of a message in an AI chat conversation.
/// </summary>
internal enum AiChatRole
{
    System,
    User,
    Assistant
}

/// <summary>
/// A single message in an AI chat conversation.
/// </summary>
internal sealed record AiChatMessage(AiChatRole Role, string Content);
