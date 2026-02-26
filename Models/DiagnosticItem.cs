using Microsoft.CodeAnalysis;

namespace KaneCode.Models;

/// <summary>
/// Represents a single diagnostic entry displayed in the error list panel.
/// </summary>
public sealed record DiagnosticItem(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string File,
    int Line,
    int Column,
    int StartOffset,
    int EndOffset,
    string FilePath)
{
    /// <summary>
    /// Icon character representing the severity level.
    /// </summary>
    public string SeverityIcon => Severity switch
    {
        DiagnosticSeverity.Error => "??",
        DiagnosticSeverity.Warning => "?",
        DiagnosticSeverity.Info => "?",
        _ => "?"
    };
}
