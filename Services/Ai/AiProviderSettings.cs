namespace KaneCode.Services.Ai;

/// <summary>
/// Persisted configuration for a single AI provider instance.
/// API keys are stored encrypted on disk and decrypted at runtime.
/// </summary>
internal sealed class AiProviderSettings
{
    /// <summary>
    /// Provider identifier (e.g. "openai", "azure-openai").
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly label for this configuration (e.g. "My OpenAI Key").
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The API endpoint URL. This can be the service root, a <c>/v1</c> base URL,
    /// or a full <c>/v1/chat/completions</c> endpoint.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The API key, stored as a Base64-encoded DPAPI-encrypted blob on disk.
    /// At runtime this property holds the plaintext value after decryption.
    /// This may be left empty for endpoints that do not require authentication.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The selected model identifier (e.g. "gpt-4o", "gpt-4.1").
    /// </summary>
    public string SelectedModel { get; set; } = string.Empty;

    /// <summary>
    /// Whether this provider entry is the active/default one.
    /// </summary>
    public bool IsActive { get; set; }
}
