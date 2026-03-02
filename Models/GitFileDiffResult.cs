namespace KaneCode.Models;

/// <summary>
/// Represents textual left/right content used by the side-by-side Git diff viewer.
/// </summary>
internal sealed record GitFileDiffResult(string RelativePath, string OriginalText, string ModifiedText);
