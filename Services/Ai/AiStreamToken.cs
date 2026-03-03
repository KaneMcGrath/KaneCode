namespace KaneCode.Services.Ai;

/// <summary>
/// Distinguishes between normal content tokens and reasoning/thinking tokens
/// streamed by reasoning models (e.g. DeepSeek-R1, QwQ).
/// </summary>
internal enum AiStreamTokenType
{
    /// <summary>Normal response content visible to the user.</summary>
    Content,

    /// <summary>Internal reasoning/chain-of-thought tokens from a reasoning model.</summary>
    Reasoning,

    /// <summary>Usage statistics emitted in the final SSE chunk.</summary>
    Usage
}

/// <summary>
/// A single streamed token from an AI provider, tagged with its type.
/// For <see cref="AiStreamTokenType.Usage"/> tokens, <see cref="UsageStats"/> is populated.
/// </summary>
internal readonly record struct AiStreamToken(AiStreamTokenType Type, string Text, AiUsageStats? UsageStats = null);

/// <summary>
/// Token usage statistics reported by the API in the final streaming chunk.
/// </summary>
internal sealed record AiUsageStats(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);
