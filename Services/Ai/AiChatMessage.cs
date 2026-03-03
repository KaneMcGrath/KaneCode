namespace KaneCode.Services.Ai;

/// <summary>
/// Role of a message in an AI chat conversation.
/// </summary>
internal enum AiChatRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>
/// A tool call requested by the assistant in its response.
/// </summary>
internal sealed record AiToolCallRequest(string Id, string FunctionName, string ArgumentsJson);

/// <summary>
/// A single message in an AI chat conversation.
/// For <see cref="AiChatRole.Assistant"/> messages that invoke tools,
/// <see cref="ToolCalls"/> contains the requested calls.
/// For <see cref="AiChatRole.Tool"/> messages, <see cref="ToolCallId"/>
/// links the result back to the originating call.
/// </summary>
internal sealed record AiChatMessage(AiChatRole Role, string Content)
{
    /// <summary>
    /// Tool calls requested by the assistant. Populated when the model
    /// responds with function calls instead of (or alongside) content.
    /// </summary>
    public IReadOnlyList<AiToolCallRequest>? ToolCalls { get; init; }

    /// <summary>
    /// The ID of the tool call this message is responding to.
    /// Set only on <see cref="AiChatRole.Tool"/> messages.
    /// </summary>
    public string? ToolCallId { get; init; }
}
