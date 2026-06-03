namespace KaneCode.Models;

/// <summary>
/// Represents a selectable build target (the solution or an individual .csproj).
/// </summary>
public sealed class BuildTargetItem
{
    /// <summary>
    /// Gets or sets the display name shown in the dropdown (e.g., "MySolution.sln" or "MyProject.csproj").
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full path to the solution file or .csproj file.
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// Returns true if this target is a .csproj file (as opposed to a .sln/.slnx solution file).
    /// </summary>
    public bool IsProject =>
        FullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if this target is a solution file (.sln or .slnx).
    /// </summary>
    public bool IsSolution =>
        FullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        FullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
}
