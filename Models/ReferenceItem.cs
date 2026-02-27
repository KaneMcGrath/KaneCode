namespace KaneCode.Models;

/// <summary>
/// Represents a single symbol reference shown in the Find References panel.
/// </summary>
public sealed record ReferenceItem(
    string SymbolName,
    string FilePath,
    string FileName,
    int Line,
    int Column,
    int StartOffset,
    string Preview);
