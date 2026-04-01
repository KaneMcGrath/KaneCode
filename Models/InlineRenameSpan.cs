namespace KaneCode.Models;

/// <summary>
/// Represents a single occurrence of a symbol in the current document for inline rename highlighting.
/// </summary>
/// <param name="Start">Zero-based start offset in the document.</param>
/// <param name="Length">Length of the symbol name span.</param>
/// <param name="IsDefinition">True when this span is the symbol's declaration site.</param>
internal sealed record InlineRenameSpan(int Start, int Length, bool IsDefinition);
