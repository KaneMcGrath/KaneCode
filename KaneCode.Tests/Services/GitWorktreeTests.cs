using KaneCode.Models;
using KaneCode.Services;
using KaneCode.Services.Tickets;
using LibGit2Sharp;
using System.IO;

namespace KaneCode.Tests.Services;

/// <summary>
/// Covers worktree discovery in <see cref="GitService"/> and the ability to open a
/// linked worktree as its own repository, which is what backs the Git Changes panel
/// when it shows another agent's worktree.
/// </summary>
public sealed class GitWorktreeTests
{
    private static string CreateTempRepositoryRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "kanecode-worktree-tests", Guid.NewGuid().ToString("N"));
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

    /// <summary>Creates a repository with a single commit and returns its root.</summary>
    private static string CreateRepositoryWithCommit()
    {
        string root = CreateTempRepositoryRoot();
        Repository.Init(root);
        File.WriteAllText(Path.Combine(root, "file.txt"), "hello");

        using Repository repository = new(root);
        Commands.Stage(repository, "*");
        Signature author = new("Test", "test@example.com", DateTimeOffset.Now);
        repository.Commit("initial commit", author, author);

        return root;
    }

    [Fact]
    public void GetWorktrees_WhenNoRepositoryIsOpen_ReturnsEmpty()
    {
        using GitService service = new();

        Assert.Empty(service.GetWorktrees());
    }

    [Fact]
    public void GetWorktrees_WithoutLinkedWorktrees_ReturnsOnlyTheMainWorktree()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            using GitService service = new();
            Assert.True(service.TryOpenRepository(root));

            IReadOnlyList<GitWorktreeInfo> worktrees = service.GetWorktrees();

            GitWorktreeInfo worktree = Assert.Single(worktrees);
            Assert.True(worktree.IsMain);
            Assert.True(worktree.IsWorkspace);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                worktree.Path,
                ignoreCase: true);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetWorktrees_ListsLinkedWorktreesWithTheirBranches()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            TicketWorktreeManager manager = new();
            string? worktreePath = manager.CreateWorktree(root, "My Ticket");
            Assert.NotNull(worktreePath);

            using GitService service = new();
            Assert.True(service.TryOpenRepository(root));

            IReadOnlyList<GitWorktreeInfo> worktrees = service.GetWorktrees();

            Assert.Equal(2, worktrees.Count);
            Assert.True(worktrees[0].IsMain);

            GitWorktreeInfo linked = worktrees[1];
            Assert.False(linked.IsMain);
            Assert.False(linked.IsWorkspace);
            Assert.Equal("My-Ticket", linked.Name);
            Assert.Equal("agent/My-Ticket", linked.BranchName);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath!)),
                linked.Path,
                ignoreCase: true);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryOpenRepository_OnALinkedWorktree_ReportsThatWorktreeAsTheWorkspace()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            TicketWorktreeManager manager = new();
            string? worktreePath = manager.CreateWorktree(root, "My Ticket");
            Assert.NotNull(worktreePath);

            // Changes made inside the worktree must show up as that worktree's status.
            File.WriteAllText(Path.Combine(worktreePath!, "agent-change.txt"), "from the agent");

            using GitService service = new();
            Assert.True(service.TryOpenRepository(worktreePath!));

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath!)),
                Path.TrimEndingDirectorySeparator(service.RepositoryWorkingDirectory!),
                ignoreCase: true);
            Assert.Equal("agent/My-Ticket", service.CurrentBranchName);
            Assert.Contains(service.GetStatus(), entry => entry.FilePath == "agent-change.txt");

            // The worktree can still see the whole repository, including the main worktree.
            IReadOnlyList<GitWorktreeInfo> worktrees = service.GetWorktrees();
            Assert.Equal(2, worktrees.Count);
            Assert.True(worktrees[0].IsMain);
            Assert.False(worktrees[0].IsWorkspace);
            Assert.True(worktrees[1].IsWorkspace);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void GetWorktrees_SkipsWorktreesWhoseDirectoryWasDeleted()
    {
        string root = CreateRepositoryWithCommit();
        try
        {
            TicketWorktreeManager manager = new();
            string? worktreePath = manager.CreateWorktree(root, "My Ticket");
            Assert.NotNull(worktreePath);

            // Delete the working directory but leave the admin entry behind.
            ForceDeleteDirectory(worktreePath!);

            using GitService service = new();
            Assert.True(service.TryOpenRepository(root));

            GitWorktreeInfo worktree = Assert.Single(service.GetWorktrees());
            Assert.True(worktree.IsMain);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }
}
