namespace KaneCode.Models;

/// <summary>
/// Represents the Git status badge displayed next to a node in the project explorer tree.
/// </summary>
public enum GitStatusBadge
{
    None,
    Modified,
    Added,
    Untracked,
    Deleted,
    Conflict
}
