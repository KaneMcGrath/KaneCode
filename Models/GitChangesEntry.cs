namespace KaneCode.Models;

/// <summary>
/// Represents a single file entry shown in the Git Changes panel.
/// </summary>
public sealed record GitChangesEntry(string RelativePath, string FullPath, GitStatusBadge Badge)
{
    /// <summary>Single-character label for the <see cref="Badge"/>.</summary>
    public string BadgeText => Badge switch
    {
        GitStatusBadge.Modified  => "M",
        GitStatusBadge.Added     => "A",
        GitStatusBadge.Untracked => "?",
        GitStatusBadge.Deleted   => "D",
        GitStatusBadge.Conflict  => "C",
        _                        => string.Empty
    };
}
