namespace KaneCode.Models;

/// <summary>
/// Represents a failed AI tool invocation shown in the AI debug panel.
/// </summary>
public sealed record AiToolFailureEntry(
    DateTimeOffset Timestamp,
    string ToolName,
    string Error,
    string Arguments,
    string ToolCallId)
{
    public string TimestampDisplay => Timestamp.LocalDateTime.ToString("HH:mm:ss");
}
