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

    /// <summary>
    /// Error message returned to the model on failure.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Optional rich, human-readable detail content displayed in the chat panel
    /// inside the tool-call section when the tool succeeds.
    ///
    /// Unlike <see cref="Output"/>, this text is <b>not</b> sent back to the
    /// model — it is UI-only, so tools can surface useful diagnostics (file
    /// stats, line ranges, diff previews) without consuming context budget.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Optional SVG markup content to be rendered inline in the chat.
    /// Set by tools like <c>draw_svg</c> whose primary purpose is visual output.
    /// </summary>
    public string? SvgContent { get; init; }

    /// <summary>Creates a successful result with the given output.</summary>
    public static ToolCallResult Ok(string output) => new() { Success = true, Output = output };

    /// <summary>
    /// Creates a successful result with concise model-facing output plus
    /// richer UI-only detail content. The <paramref name="output"/> text is
    /// returned to the model; <paramref name="details"/> is rendered only in
    /// the chat panel's tool-call section.
    /// </summary>
    public static ToolCallResult OkWithDetails(string output, string details) => new()
    {
        Success = true,
        Output = output,
        Details = details
    };

    /// <summary>
    /// Creates a successful result with SVG content to be rendered inline.
    /// The <paramref name="output"/> text is returned to the model;
    /// the <paramref name="svgContent"/> is displayed inline in the chat UI.
    /// </summary>
    public static ToolCallResult OkWithSvg(string output, string svgContent) => new()
    {
        Success = true,
        Output = output,
        SvgContent = svgContent
    };

    /// <summary>Creates a failed result with the given error message.</summary>
    public static ToolCallResult Fail(string error) => new() { Success = false, Error = error };

    /// <summary>
    /// Creates a conflict result indicating that a file lock could not be acquired.
    /// This signals to the model that it should wait and retry the operation.
    /// </summary>
    public static ToolCallResult Conflict(string message) => new()
    {
        Success = false,
        Error = $"[CONFLICT] {message}"
    };
}
