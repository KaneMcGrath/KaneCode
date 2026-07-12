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
/// An image attached to a user message for vision-capable models.
/// </summary>
internal sealed record AiChatImagePart(string Base64Data, string MimeType);

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
    /// Thinking/reasoning text associated with the assistant message.
    /// This is retained for UI rendering and is sent back to the model as
    /// <c>reasoning_content</c> on subsequent assistant messages so that
    /// reasoning-capable providers can validate the conversation state.
    /// </summary>
    public string? ThinkingContent { get; init; }

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

    /// <summary>
    /// Images attached to a user message for vision-capable providers.
    /// Each entry contains base64-encoded image data and its MIME type.
    /// When set, the provider serializes <c>content</c> as an array of
    /// <c>text</c> and <c>image_url</c> parts per the OpenAI vision format.
    /// </summary>
    public IReadOnlyList<AiChatImagePart>? Images { get; init; }
}
