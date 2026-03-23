namespace KaneCode.Services.Ai;

/// <summary>
/// Persisted configuration for a single AI provider instance.
/// API keys are stored encrypted on disk and decrypted at runtime.
/// </summary>
internal sealed class AiProviderSettings
{
    public const double DefaultTemperature = 0.6;
    public const double DefaultTopP = 0.95;
    public const int DefaultTopK = 20;
    public const double DefaultMinP = 0.0;
    public const double DefaultPresencePenalty = 0.0;
    public const double DefaultRepetitionPenalty = 1.0;

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
    /// Optional sampling temperature for OpenAI-compatible providers.
    /// </summary>
    public double? Temperature { get; set; } = DefaultTemperature;

    /// <summary>
    /// Optional nucleus sampling value for OpenAI-compatible providers.
    /// </summary>
    public double? TopP { get; set; } = DefaultTopP;

    /// <summary>
    /// Optional top-k sampling value for compatible providers.
    /// </summary>
    public int? TopK { get; set; } = DefaultTopK;

    /// <summary>
    /// Optional minimum probability threshold for compatible providers.
    /// </summary>
    public double? MinP { get; set; } = DefaultMinP;

    /// <summary>
    /// Optional presence penalty for OpenAI-compatible providers.
    /// </summary>
    public double? PresencePenalty { get; set; } = DefaultPresencePenalty;

    /// <summary>
    /// Optional repetition penalty for compatible providers.
    /// </summary>
    public double? RepetitionPenalty { get; set; } = DefaultRepetitionPenalty;
}
