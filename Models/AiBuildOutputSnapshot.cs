namespace KaneCode.Models;

/// <summary>
/// Captures the current build output text shown in the UI so it can be attached as AI context.
/// </summary>
internal sealed record AiBuildOutputSnapshot(string Summary, IReadOnlyList<string> Lines);
