namespace KaneCode.Models;

/// <summary>
/// The kind of change a file has inside a ticket's Git worktree, relative to the
/// commit the agent started from.
/// </summary>
public enum TicketWorktreeChangeKind
{
    /// <summary>The file exists in the worktree but not in the base commit.</summary>
    Added,

    /// <summary>The file exists in both the base commit and the worktree, with different content.</summary>
    Modified,

    /// <summary>The file exists in the base commit but is gone from the worktree.</summary>
    Deleted
}

/// <summary>
/// A single file changed inside a ticket's Git worktree. The Tickets panel lists
/// these so the user can review an agent's work before merging it into the main
/// IDE worktree or committing it.
/// </summary>
public sealed record TicketWorktreeChange(string RelativePath, TicketWorktreeChangeKind Kind)
{
    /// <summary>Single-character glyph used to render the change kind in the file list.</summary>
    public string KindGlyph => Kind switch
    {
        TicketWorktreeChangeKind.Added => "+",
        TicketWorktreeChangeKind.Deleted => "-",
        TicketWorktreeChangeKind.Modified => "~",
        _ => "·"
    };
}
