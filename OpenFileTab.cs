namespace KaneCode;

/// <summary>
/// Tracks an open file tab in the editor.
/// </summary>
public sealed class OpenFileTab : ObservableObject
{
    public OpenFileTab(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
    }

    /// <summary>Full path of the open file.</summary>
    public string FilePath { get; }

    /// <summary>Display name for the tab.</summary>
    public string FileName { get; }

    private bool _isDirty;
    /// <summary>True when the file has unsaved changes.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (SetProperty(ref _isDirty, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>Tab header text, showing an asterisk when dirty.</summary>
    public string DisplayName => IsDirty ? $"{FileName} *" : FileName;
}
