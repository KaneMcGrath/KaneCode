using KaneCode.Models;
using KaneCode.Services.Tickets;
using LibGit2Sharp;
using System.IO;

namespace KaneCode.Tests.Services.Tickets;

/// <summary>
/// Covers reviewing and applying a ticket agent's worktree changes: listing the
/// changed files, copying them into the main IDE worktree (merge), and committing
/// them on the current branch (commit).
/// </summary>
public sealed class TicketWorktreeMergeTests
{
    private static string CreateTempRepositoryRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "kanecode-worktree-merge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Deletes a directory tree even when it contains read-only files (git marks loose
    /// object files read-only, which makes plain recursive deletion throw on Windows).
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Creates a repository with a single commit containing <c>file.txt</c> and
    /// <c>delete-me.txt</c>, and returns its root.
    /// </summary>
    private static string CreateRepositoryWithCommit()
    {
        string root = CreateTempRepositoryRoot();
        Repository.Init(root);
        File.WriteAllText(Path.Combine(root, "file.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "delete-me.txt"), "bye");

        using Repository repository = new(root);
        Commands.Stage(repository, "*");
        Signature author = new("Test", "test@example.com", DateTimeOffset.Now);
        repository.Commit("initial commit", author, author);

        return root;
    }

    /// <summary>
    /// Creates the repository + a ticket worktree, then makes the standard agent
    /// changes in the worktree: modify <c>file.txt</c>, add <c>new.txt</c>, delete
    /// <c>delete-me.txt</c>, and drop an untracked <c>.kanecode</c> note.
    /// </summary>
    private static (string Root, string WorktreePath) CreateWorktreeWithChanges()
    {
        string root = CreateRepositoryWithCommit();
        TicketWorktreeManager manager = new();
        string? worktreePath = manager.CreateWorktree(root, "My Ticket");
        Assert.NotNull(worktreePath);

        File.WriteAllText(Path.Combine(worktreePath!, "file.txt"), "changed");
        File.WriteAllText(Path.Combine(worktreePath!, "new.txt"), "new file");
        File.Delete(Path.Combine(worktreePath!, "delete-me.txt"));

        string kanecodeDir = Path.Combine(worktreePath!, ".kanecode", "tickets");
        Directory.CreateDirectory(kanecodeDir);
        File.WriteAllText(Path.Combine(kanecodeDir, "agent-note.txt"), "ide state, not agent work");

        return (root, worktreePath!);
    }

    [Fact]
    public void TryGetWorktreePath_ReturnsDeterministicPathForTicket()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            string? path = TicketWorktreeManager.TryGetWorktreePath(root, "My Ticket");
            Assert.NotNull(path);
            Assert.EndsWith(Path.Combine(".kanecode", "worktrees", "My-Ticket"), path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetWorktreeChanges_WhenWorktreeIsClean_ReturnsEmpty()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            TicketWorktreeManager manager = new();
            string? worktreePath = manager.CreateWorktree(root, "My Ticket");
            Assert.NotNull(worktreePath);

            Assert.Empty(manager.GetWorktreeChanges(root, worktreePath!));
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetWorktreeChanges_ListsUncommittedChangesWithKinds()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            TicketWorktreeManager manager = new();
            string? worktreePath = manager.CreateWorktree(root, "My Ticket");
            Assert.NotNull(worktreePath);

            File.WriteAllText(Path.Combine(worktreePath!, "file.txt"), "changed");
            File.WriteAllText(Path.Combine(worktreePath!, "new.txt"), "new file");
            File.Delete(Path.Combine(worktreePath!, "delete-me.txt"));

            IReadOnlyList<TicketWorktreeChange> changes = manager.GetWorktreeChanges(root, worktreePath!);

            Assert.Equal(3, changes.Count);
            Assert.Contains(changes, change =>
                change.RelativePath == "delete-me.txt" && change.Kind == TicketWorktreeChangeKind.Deleted);
            Assert.Contains(changes, change =>
                change.RelativePath == "file.txt" && change.Kind == TicketWorktreeChangeKind.Modified);
            Assert.Contains(changes, change =>
                change.RelativePath == "new.txt" && change.Kind == TicketWorktreeChangeKind.Added);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetWorktreeChanges_ListsCommittedChanges()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            TicketWorktreeManager manager = new();
            string? worktreePath = manager.CreateWorktree(root, "My Ticket");
            Assert.NotNull(worktreePath);

            // The agent commits its work inside the worktree, on agent/My-Ticket.
            using (Repository worktree = new(worktreePath!))
            {
                File.WriteAllText(Path.Combine(worktreePath!, "file.txt"), "changed");
                File.WriteAllText(Path.Combine(worktreePath!, "new.txt"), "new file");
                File.Delete(Path.Combine(worktreePath!, "delete-me.txt"));

                Commands.Stage(worktree, "*");
                Signature author = new("Agent", "agent@example.com", DateTimeOffset.Now);
                worktree.Commit("agent work", author, author);
            }

            IReadOnlyList<TicketWorktreeChange> changes = manager.GetWorktreeChanges(root, worktreePath!);

            Assert.Equal(3, changes.Count);
            Assert.Contains(changes, change =>
                change.RelativePath == "delete-me.txt" && change.Kind == TicketWorktreeChangeKind.Deleted);
            Assert.Contains(changes, change =>
                change.RelativePath == "file.txt" && change.Kind == TicketWorktreeChangeKind.Modified);
            Assert.Contains(changes, change =>
                change.RelativePath == "new.txt" && change.Kind == TicketWorktreeChangeKind.Added);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetWorktreeChanges_ExcludesKanecodePaths()
    {
        (string root, string worktreePath) = CreateWorktreeWithChanges();
        try
        {
            TicketWorktreeManager manager = new();
            IReadOnlyList<TicketWorktreeChange> changes = manager.GetWorktreeChanges(root, worktreePath);

            Assert.Equal(3, changes.Count);
            Assert.DoesNotContain(changes, change => change.RelativePath.StartsWith(".kanecode", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void ApplyWorktreeChangesToWorkspace_CopiesChangesToMainWorktree()
    {
        (string root, string worktreePath) = CreateWorktreeWithChanges();
        try
        {
            TicketWorktreeManager manager = new();

            int applied = manager.ApplyWorktreeChangesToWorkspace(root, worktreePath);

            Assert.Equal(3, applied);
            Assert.Equal("changed", File.ReadAllText(Path.Combine(root, "file.txt")));
            Assert.Equal("new file", File.ReadAllText(Path.Combine(root, "new.txt")));
            Assert.False(File.Exists(Path.Combine(root, "delete-me.txt")));

            // Nothing is staged or committed by a merge.
            using Repository main = new(root);
            RepositoryStatus status = main.RetrieveStatus();
            Assert.Contains(status, entry => entry.FilePath == "file.txt");
            Assert.Contains(status, entry => entry.FilePath == "new.txt" && entry.State.HasFlag(FileStatus.NewInWorkdir));
            Assert.Contains(status, entry => entry.FilePath == "delete-me.txt" && entry.State.HasFlag(FileStatus.DeletedFromWorkdir));
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void ApplyWorktreeChangesToWorkspace_LeavesUnrelatedMainChangesAlone()
    {
        (string root, string worktreePath) = CreateWorktreeWithChanges();
        try
        {
            // The user has their own local edit in the main worktree.
            string unrelatedPath = Path.Combine(root, "user-note.txt");
            File.WriteAllText(unrelatedPath, "user's own work");

            TicketWorktreeManager manager = new();
            int applied = manager.ApplyWorktreeChangesToWorkspace(root, worktreePath);

            Assert.Equal(3, applied);
            Assert.True(File.Exists(unrelatedPath));
            Assert.Equal("user's own work", File.ReadAllText(unrelatedPath));
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void CommitWorktreeChanges_CommitsOnlyAgentFilesOnMainBranch()
    {
        (string root, string worktreePath) = CreateWorktreeWithChanges();
        try
        {
            // The user has an unrelated untracked file that must not be swept into the commit.
            string unrelatedPath = Path.Combine(root, "user-note.txt");
            File.WriteAllText(unrelatedPath, "user's own work");

            TicketWorktreeManager manager = new();
            int applied = manager.CommitWorktreeChanges(root, worktreePath, "Implement the ticket");

            Assert.Equal(3, applied);

            using Repository main = new(root);
            Assert.Equal("Implement the ticket", main.Head.Tip.MessageShort);

            Assert.NotNull(main.Head.Tip.Tree["file.txt"]);
            Assert.NotNull(main.Head.Tip.Tree["new.txt"]);
            Assert.Null(main.Head.Tip.Tree["delete-me.txt"]);
            Assert.Null(main.Head.Tip.Tree["user-note.txt"]);

            // The agent's files are committed; the user's untracked file is untouched.
            RepositoryStatus status = main.RetrieveStatus();
            Assert.DoesNotContain(status, entry => entry.FilePath is "file.txt" or "new.txt" or "delete-me.txt");
            Assert.Contains(status, entry => entry.FilePath == "user-note.txt");
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void CommitWorktreeChanges_WhenWorktreeIsClean_ReturnsZeroAndDoesNotCommit()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            TicketWorktreeManager manager = new();
            string? worktreePath = manager.CreateWorktree(root, "My Ticket");
            Assert.NotNull(worktreePath);

            int applied = manager.CommitWorktreeChanges(root, worktreePath!, "No-op commit");

            Assert.Equal(0, applied);

            using Repository main = new(root);
            Assert.Equal("initial commit", main.Head.Tip.MessageShort);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void CommitWorktreeChanges_WithEmptyMessage_Throws()
    {
        (string root, string worktreePath) = CreateWorktreeWithChanges();
        try
        {
            TicketWorktreeManager manager = new();

            Assert.Throws<ArgumentException>(
                () => manager.CommitWorktreeChanges(root, worktreePath, "   "));
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }
}
