namespace KaneCode.Services.Ai;

/// <summary>
/// Result returned by an <see cref="IAgentTool"/> after execution.
/// Sent back to the model as the tool response content.
/// </summary>
internal sealed record ToolCallResult
{
    /// <summary>Whether the tool executed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Output text returned to the model on success.
    /// Should be concise to conserve context budget.
    /// </summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Error message returned to the model on failure.</summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result with the given output.</summary>
    public static ToolCallResult Ok(string output) => new() { Success = true, Output = output };

    /// <summary>Creates a failed result with the given error message.</summary>
    public static ToolCallResult Fail(string error) => new() { Success = false, Error = error };
}
