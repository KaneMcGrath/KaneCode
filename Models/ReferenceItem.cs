namespace KaneCode.Models;

/// <summary>
/// Classifies a navigation result shown in the Find References panel.
/// </summary>
public enum ReferenceKind
{
    Definition,
    Reference,
    Implementation
}

/// <summary>
/// Represents a single symbol reference shown in the Find References panel.
/// </summary>
public sealed record ReferenceItem(
    string SymbolName,
    string FilePath,
    string FileName,
    string ProjectName,
    int Line,
    int Column,
    int StartOffset,
    string Preview,
    ReferenceKind Kind)
{
    public string KindDisplayName => Kind switch
    {
        ReferenceKind.Definition => "Definition",
        ReferenceKind.Implementation => "Implementation",
        _ => "Reference"
    };

    public string KindIcon => Kind switch
    {
        ReferenceKind.Definition => "📘",
        ReferenceKind.Implementation => "🔧",
        _ => "🔎"
    };
}
