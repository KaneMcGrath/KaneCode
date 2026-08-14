using System.IO;
using LibGit2Sharp;

namespace KaneCode.Services.Tickets;

/// <summary>
/// Creates and removes one Git worktree per active ticket so autonomous agents can
/// edit, build, and commit in isolation without mutating the user's active workspace.
///
/// Worktrees are placed under <c>.kanecode/worktrees/&lt;ticket-id&gt;</c> and use a
/// private branch <c>agent/&lt;ticket-id&gt;</c> rooted at the captured base commit.
/// The same repository object store backs every worktree, so commits land in normal
/// Git history and can later be reviewed/merged by the user.
/// </summary>
internal sealed class TicketWorktreeManager
{
    /// <summary>Subfolder under <c>.kanecode</c> that holds per-ticket worktrees.</summary>
    internal const string WorktreesFolderName = "worktrees";

    /// <summary>Branch namespace used for per-ticket agent branches.</summary>
    internal const string AgentBranchPrefix = "agent/";

    /// <summary>
    /// Returns the directory that holds per-ticket worktrees for the given repository
    /// working directory, or null when no repository is provided.
    /// </summary>
    public static string? TryGetWorktreesDirectory(string? repositoryWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(repositoryWorkingDirectory))
        {
            return null;
        }

        return Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryWorkingDirectory)),
            TicketFileStore.KaneCodeFolderName,
            WorktreesFolderName);
    }

    /// <summary>
    /// Creates a worktree for a ticket. The worktree is created at the current HEAD of
    /// the repository on a new private branch <c>agent/&lt;ticketId&gt;</c>. Returns the
    /// created worktree's root directory, or null when the repository is unavailable.
    /// </summary>
    public string? CreateWorktree(string repositoryPath, string ticketId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);

        string safeTicketId = SanitizeTicketId(ticketId);
        string worktreesRoot = TryGetWorktreesDirectory(repositoryPath)
            ?? throw new InvalidOperationException("No repository working directory is available.");

        Directory.CreateDirectory(worktreesRoot);

        string worktreePath = Path.Combine(worktreesRoot, safeTicketId);
        if (Directory.Exists(worktreePath))
        {
            // A previous worktree for this ticket already exists; reuse it.
            return worktreePath;
        }

        using Repository repository = new(repositoryPath);
        string branchName = AgentBranchPrefix + safeTicketId;

        if (repository.Info.IsHeadUnborn || repository.Head?.Tip is null)
        {
            throw new InvalidOperationException(
                $"the Git repository has no commits yet, so an isolated worktree cannot be created. " +
                "Make an initial commit (or check out an existing commit) and try again.");
        }

        // Remove a stale branch with the same name so the new worktree starts fresh.
        Branch? staleBranch = repository.Branches[branchName];
        if (staleBranch is not null && !staleBranch.IsRemote)
        {
            if (!repository.Info.IsHeadDetached &&
                string.Equals(repository.Head.FriendlyName, branchName, StringComparison.OrdinalIgnoreCase))
            {
                // Do not delete the branch we are currently on.
            }
            else
            {
                repository.Branches.Remove(staleBranch);
            }
        }

        Branch branch = repository.CreateBranch(branchName);
        repository.Worktrees.Add(branch.CanonicalName, safeTicketId, worktreePath, isLocked: false);
        return worktreePath;
    }

    /// <summary>
    /// Removes the worktree for a ticket (if present) and deletes its private branch,
    /// then cleans up the repository's worktree administration entry.
    /// </summary>
    public void RemoveWorktree(string repositoryPath, string ticketId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);

        string safeTicketId = SanitizeTicketId(ticketId);

        // Delete the worktree working directory on disk. Processes must be stopped
        // by the caller before this is invoked, or files may be held open.
        string? worktreesRoot = TryGetWorktreesDirectory(repositoryPath);
        if (!string.IsNullOrWhiteSpace(worktreesRoot))
        {
            string worktreePath = Path.Combine(worktreesRoot, safeTicketId);
            TryDeleteDirectory(worktreePath);
        }

        try
        {
            using Repository repository = new(repositoryPath);

            // Remove the repository's worktree administration entry (the
            // .git/worktrees/<name> folder) so stale worktrees do not accumulate.
            string? gitDirectory = Repository.Discover(repositoryPath);
            if (!string.IsNullOrWhiteSpace(gitDirectory) && Directory.Exists(gitDirectory))
            {
                string adminPath = Path.Combine(gitDirectory, "worktrees", safeTicketId);
                TryDeleteDirectory(adminPath);
            }

            Branch? branch = repository.Branches[AgentBranchPrefix + safeTicketId];
            if (branch is not null && !branch.IsRemote)
            {
                try
                {
                    repository.Branches.Remove(branch);
                }
                catch (LibGit2SharpException)
                {
                    // Best effort — the branch may already be deleted.
                }
            }
        }
        catch (RepositoryNotFoundException)
        {
            // No repository to clean up.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort — files may be locked by a running process.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Maps a user project path to its equivalent inside a ticket worktree. When the
    /// user's project is a file inside the repository (a solution or project file), the
    /// relative path is recomputed against the worktree root. Otherwise the worktree
    /// root directory itself is returned.
    /// </summary>
    public static string? ComputeWorktreeProjectPath(
        string? worktreeRoot,
        string? userProjectPath,
        string? repositoryWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(worktreeRoot))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(userProjectPath))
        {
            return worktreeRoot;
        }

        string fullUserPath = Path.GetFullPath(userProjectPath);

        if (!string.IsNullOrWhiteSpace(repositoryWorkingDirectory) && File.Exists(fullUserPath))
        {
            string fullRepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryWorkingDirectory));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (fullUserPath.StartsWith(fullRepositoryRoot + Path.DirectorySeparatorChar, comparison))
            {
                string relative = Path.GetRelativePath(fullRepositoryRoot, fullUserPath);
                return Path.Combine(worktreeRoot, relative);
            }
        }

        return worktreeRoot;
    }

    private static string SanitizeTicketId(string ticketId)
    {
        System.Text.StringBuilder builder = new(ticketId.Length);
        foreach (char c in ticketId)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        // Git worktree/ref names cannot start or end with '-' or be empty, so trim
        // any leading/trailing separators produced by a title that begins or ends
        // with non-alphanumeric characters.
        string sanitized = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(sanitized) ? "ticket" : sanitized;
    }
}
