namespace KaneCode.Models;

/// <summary>
/// Captures the current text for an editor document so it can be attached as AI context.
/// </summary>
internal sealed record AiContextDocumentSnapshot(string FilePath, string DisplayName, string Content);
