namespace KaneCode.Models;

/// <summary>
/// Represents a single commit row displayed in the Git Log panel.
/// </summary>
public sealed record GitCommitLogItem(
    string ShortHash,
    string FullHash,
    string Message,
    string Author,
    DateTimeOffset Date);
