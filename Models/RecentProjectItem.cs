namespace KaneCode.Models;

/// <summary>
/// Classifies what kind of item a <see cref="RecentProjectItem"/> represents.
/// </summary>
public enum RecentItemType
{
    /// <summary>A single .csproj project file.</summary>
    Project,
    /// <summary>A .sln or .slnx solution file.</summary>
    Solution,
    /// <summary>A folder opened as the project root.</summary>
    Folder
}

/// <summary>
/// Represents a recently opened project, solution, or folder.
/// </summary>
public sealed class RecentProjectItem
{
    public RecentProjectItem(string fullPath, RecentItemType itemType, DateTime lastOpened)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        FullPath = fullPath;
        ItemType = itemType;
        LastOpened = lastOpened;
        DisplayName = System.IO.Path.GetFileName(fullPath);
    }

    /// <summary>Absolute path to the project, solution, or folder.</summary>
    public string FullPath { get; }

    /// <summary>The kind of item (Project, Solution, or Folder).</summary>
    public RecentItemType ItemType { get; }

    /// <summary>Timestamp of when this item was last opened.</summary>
    public DateTime LastOpened { get; set; }

    /// <summary>Display name (file or folder name only).</summary>
    public string DisplayName { get; }

    /// <summary>Emoji icon derived from <see cref="ItemType"/>.</summary>
    public string Icon => ItemType switch
    {
        RecentItemType.Solution => "🗂️",
        RecentItemType.Project => "📦",
        RecentItemType.Folder => "📁",
        _ => "📄"
    };

    /// <summary>Tooltip showing the full path.</summary>
    public string ToolTip => FullPath;
}
