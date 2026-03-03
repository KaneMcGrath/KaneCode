namespace KaneCode.Services.Ai;

/// <summary>
/// Abstraction for an AI completion provider (e.g. OpenAI, Azure OpenAI, local Llama.cpp).
/// Implementations translate requests into provider-specific HTTP calls and stream tokens back.
/// </summary>
internal interface IAiProvider
{
    /// <summary>
    /// Human-readable name shown in the UI (e.g. "OpenAI", "Azure OpenAI", "Llama.cpp (local)").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Unique identifier for this provider used in settings persistence.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Whether the provider has been configured with the minimum required settings
    /// (e.g. an API key or endpoint) to make requests.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends a chat completion request and streams response tokens as they arrive.
    /// </summary>
    /// <param name="messages">The conversation messages to send.</param>
    /// <param name="model">The model identifier to use (e.g. "gpt-4o").</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>An async enumerable of response token strings.</returns>
    IAsyncEnumerable<string> StreamCompletionAsync(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of model identifiers available for this provider.
    /// </summary>
    IReadOnlyList<string> AvailableModels { get; }
}
