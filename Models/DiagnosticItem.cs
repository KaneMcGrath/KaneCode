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
    string FilePath,
    string Category = "",
    string Project = "")
{
    /// <summary>
    /// Icon character representing the severity level.
    /// </summary>
    public string SeverityIcon => Severity switch
    {
        DiagnosticSeverity.Error => "\u274C",
        DiagnosticSeverity.Warning => "\u26A0",
        DiagnosticSeverity.Info => "\u2139",
        _ => "\u2753"
    };

    /// <summary>
    /// Combined source label showing the diagnostic code and category.
    /// </summary>
    public string Source => string.IsNullOrEmpty(Category) ? Code : $"{Code} ({Category})";
}
