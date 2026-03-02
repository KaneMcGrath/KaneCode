using KaneCode.Infrastructure;
using System.Collections.ObjectModel;
using System.IO;

namespace KaneCode.Models;

/// <summary>
/// Classifies what kind of node a <see cref="ProjectItem"/> represents.
/// </summary>
public enum ProjectItemType
{
    File,
    Folder,
    Project,
    Solution
}

/// <summary>
/// Represents a file or directory node in the project explorer tree.
/// </summary>
public sealed class ProjectItem : ObservableObject
{
    public ProjectItem(string fullPath, bool isDirectory)
        : this(fullPath, isDirectory ? ProjectItemType.Folder : ProjectItemType.File)
    {
    }

    public ProjectItem(string fullPath, ProjectItemType itemType)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        ItemType = itemType;
    }

    /// <summary>Absolute path to the file or directory.</summary>
    public string FullPath { get; }

    /// <summary>Display name (file/folder name only).</summary>
    public string Name { get; }

    /// <summary>The kind of node (Solution, Project, Folder, File).</summary>
    public ProjectItemType ItemType { get; }

    /// <summary>True when this node represents a directory, project, or solution.</summary>
    public bool IsDirectory => ItemType is ProjectItemType.Folder
                            or ProjectItemType.Project
                            or ProjectItemType.Solution;

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

    private GitStatusBadge _gitBadge;
    /// <summary>Git status badge shown next to the file name in the explorer tree.</summary>
    public GitStatusBadge GitBadge
    {
        get => _gitBadge;
        set
        {
            if (SetProperty(ref _gitBadge, value))
            {
                OnPropertyChanged(nameof(GitBadgeText));
            }
        }
    }

    /// <summary>Single-character label for the current <see cref="GitBadge"/>; empty when <see cref="GitStatusBadge.None"/>.</summary>
    public string GitBadgeText => _gitBadge switch
    {
        GitStatusBadge.Modified  => "M",
        GitStatusBadge.Added     => "A",
        GitStatusBadge.Untracked => "?",
        GitStatusBadge.Deleted   => "D",
        GitStatusBadge.Conflict  => "C",
        _                        => string.Empty
    };

    /// <summary>Emoji icon derived from <see cref="ItemType"/> and file extension.</summary>
    public string Icon => ItemType switch
    {
        ProjectItemType.Solution => "🗂️",
        ProjectItemType.Project => "📦",
        ProjectItemType.Folder => "📁",
        ProjectItemType.File => GetFileIcon(),
        _ => "📄"
    };

    private string GetFileIcon()
    {
        var ext = Path.GetExtension(FullPath);
        return ext.ToLowerInvariant() switch
        {
            ".cs" => "🔷",
            ".xaml" => "🖼️",
            ".csproj" => "📦",
            ".sln" => "🗂️",
            _ => "📄"
        };
    }
}
