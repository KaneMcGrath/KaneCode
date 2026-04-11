namespace KaneCode.Models;

/// <summary>
/// Captures a discovered C# class and the source text used when it is attached as AI context.
/// </summary>
internal sealed record AiContextClassSnapshot(string DisplayName, string FilePath, string SourceText);
