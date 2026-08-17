namespace KaneCode.Models;

/// <summary>
/// Describes one Git worktree of the open repository: either the main working
/// directory or a linked worktree (for example the isolated worktrees the ticket
/// system creates for autonomous agents).
/// </summary>
/// <param name="Name">Worktree name — the folder name for the main worktree, the Git worktree name otherwise.</param>
/// <param name="Path">Absolute path to the worktree working directory.</param>
/// <param name="BranchName">Branch checked out in the worktree, or <see langword="null"/> when HEAD is detached.</param>
/// <param name="IsMain">Whether this is the repository's main working directory.</param>
/// <param name="IsWorkspace">Whether the IDE currently has this worktree loaded as its workspace.</param>
public sealed record GitWorktreeInfo(
    string Name,
    string Path,
    string? BranchName,
    bool IsMain,
    bool IsWorkspace)
{
    /// <summary>Label shown in the worktree selector.</summary>
    public string DisplayName =>
        $"{Name} ({BranchName ?? "detached HEAD"})";

    /// <summary>
    /// Label shown in the Git menu, marked when the IDE already works in this worktree.
    /// The marker is part of the text so it renders the same under every theme.
    /// </summary>
    public string MenuDisplayName =>
        IsWorkspace ? $"✓ {DisplayName}" : $"    {DisplayName}";
}
