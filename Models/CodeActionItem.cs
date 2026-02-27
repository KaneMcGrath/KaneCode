using Microsoft.CodeAnalysis.CodeActions;

namespace KaneCode.Models;

/// <summary>
/// Represents a single code action (fix or refactoring) shown in the lightbulb popup.
/// </summary>
public sealed record CodeActionItem(
    string Title,
    CodeAction Action);
