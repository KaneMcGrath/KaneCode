using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Abstraction for an AI completion provider (e.g. OpenAI or Azure OpenAI).
/// Implementations translate requests into provider-specific HTTP calls and stream tokens back.
/// </summary>
internal interface IAiProvider
{
    /// <summary>
    /// Human-readable name shown in the UI (e.g. "OpenAI" or "Azure OpenAI").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Unique identifier for this provider used in settings persistence.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Whether the provider has been configured with the minimum required settings
    /// (e.g. an endpoint, with an optional API key when required) to make requests.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends a chat completion request and streams response tokens as they arrive.
    /// Reasoning models may emit <see cref="AiStreamTokenType.Reasoning"/> tokens
    /// before the main <see cref="AiStreamTokenType.Content"/> tokens.
    /// When tools are provided, the model may emit <see cref="AiStreamTokenType.ToolCall"/> tokens.
    /// </summary>
    /// <param name="messages">The conversation messages to send.</param>
    /// <param name="model">The model identifier to use (e.g. "gpt-4o").</param>
    /// <param name="tools">
    /// Optional serialized tools array in OpenAI format.
    /// Pass <c>default</c> to omit tools from the request.
    /// </param>
    /// <param name="streamResponse">
    /// True to request server-side streaming from the provider; false to request a buffered response.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>An async enumerable of streamed tokens.</returns>
    IAsyncEnumerable<AiStreamToken> StreamCompletionAsync(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        JsonElement tools = default,
        bool streamResponse = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of model identifiers available for this provider.
    /// </summary>
    IReadOnlyList<string> AvailableModels { get; }
}
