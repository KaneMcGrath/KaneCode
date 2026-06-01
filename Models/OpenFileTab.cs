using ICSharpCode.AvalonEdit.Document;
using KaneCode.Infrastructure;

namespace KaneCode.Models;

/// <summary>
/// Tracks an open file tab in the editor, including its text content and undo history.
/// </summary>
public sealed class OpenFileTab : ObservableObject
{
    public OpenFileTab(string filePath) : this(filePath, string.Empty)
    {
    }

    public OpenFileTab(string filePath, string content)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        Document = new TextDocument(content);
    }

    /// <summary>Full path of the open file.</summary>
    public string FilePath { get; }

    /// <summary>Display name for the tab.</summary>
    public string FileName { get; }

    /// <summary>
    /// The AvalonEdit document for this tab. Each tab owns its own document
    /// so that undo/redo history is preserved across tab switches.
    /// </summary>
    public TextDocument Document { get; }

    private bool _isDirty;
    /// <summary>True when the file has unsaved changes.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>Tab header text, showing an asterisk when dirty.</summary>
    public string DisplayName => IsDirty ? $"{FileName} *" : FileName;

    private double _scrollOffset;
    /// <summary>
    /// The vertical scroll offset of the editor for this tab.
    /// Saved when switching away and restored when switching back.
    /// </summary>
    public double ScrollOffset
    {
        get => _scrollOffset;
        set => SetProperty(ref _scrollOffset, value);
    }

    private bool _isMarkdownPreviewActive;
    /// <summary>
    /// True when the markdown preview is shown instead of the source editor.
    /// Only meaningful for .md files; ignored for other file types.
    /// </summary>
    public bool IsMarkdownPreviewActive
    {
        get => _isMarkdownPreviewActive;
        set => SetProperty(ref _isMarkdownPreviewActive, value);
    }
}
