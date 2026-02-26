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
}
