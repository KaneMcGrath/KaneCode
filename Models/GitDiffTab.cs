using KaneCode.Infrastructure;
using System.IO;

namespace KaneCode.Models;

/// <summary>
/// Represents a Git diff shown as a tab in the main editor area. Diff tabs are
/// opened on demand (e.g., from the Git Changes panel) instead of living in a
/// permanently docked panel, and can be closed independently of file tabs.
/// </summary>
public sealed class GitDiffTab : ObservableObject
{
    public GitDiffTab(string relativePath, string originalText, string modifiedText)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        RelativePath = relativePath;
        OriginalText = originalText;
        ModifiedText = modifiedText;
        FileName = Path.GetFileName(relativePath);
    }

    /// <summary>Repository-relative path of the changed file.</summary>
    public string RelativePath { get; }

    /// <summary>File name used for the tab header.</summary>
    public string FileName { get; }

    /// <summary>Left-side (HEAD / original) content of the diff.</summary>
    public string OriginalText { get; private set; }

    /// <summary>Right-side (working tree / modified) content of the diff.</summary>
    public string ModifiedText { get; private set; }

    /// <summary>Tab header text.</summary>
    public string DisplayName => $"Diff: {FileName}";

    /// <summary>
    /// Refreshes the diff content when the same file's diff is requested again,
    /// so re-opening a diff always shows up-to-date content.
    /// </summary>
    public void Update(string originalText, string modifiedText)
    {
        OriginalText = originalText;
        ModifiedText = modifiedText;
    }
}
