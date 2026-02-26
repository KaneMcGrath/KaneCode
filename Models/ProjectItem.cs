using KaneCode.Infrastructure;
using System.Collections.ObjectModel;
using System.IO;

namespace KaneCode.Models;

/// <summary>
/// Represents a file or directory node in the project explorer tree.
/// </summary>
public sealed class ProjectItem : ObservableObject
{
    public ProjectItem(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        IsDirectory = isDirectory;
    }

    /// <summary>Absolute path to the file or directory.</summary>
    public string FullPath { get; }

    /// <summary>Display name (file/folder name only).</summary>
    public string Name { get; }

    /// <summary>True when this node represents a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Child items (populated for directories).</summary>
    public ObservableCollection<ProjectItem> Children { get; } = [];

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
