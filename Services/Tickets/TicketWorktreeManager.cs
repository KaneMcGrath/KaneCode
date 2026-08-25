using System.IO;
using KaneCode.Models;
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
///
/// The manager also reads a ticket worktree's changed files and applies them to the
/// main IDE worktree, which is what the Tickets panel's merge/commit buttons use.
/// </summary>
internal sealed class TicketWorktreeManager
{
    /// <summary>Subfolder under <c>.kanecode</c> that holds per-ticket worktrees.</summary>
    internal const string WorktreesFolderName = "worktrees";

    /// <summary>Branch namespace used for per-ticket agent branches.</summary>
    internal const string AgentBranchPrefix = "agent/";

    /// <summary>Paths under this folder inside a worktree are IDE state, never agent work.</summary>
    private const string KaneCodeFolderPrefix = ".kanecode/";

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
    /// Returns the deterministic worktree directory for a ticket, or null when no
    /// repository is available. The directory may not exist yet — it is created on
    /// the first dispatch. This lets the Tickets panel find a finished ticket's
    /// worktree even though <see cref="KaneCodeTicket.WorktreePath"/> is only kept in
    /// memory while the ticket is running.
    /// </summary>
    public static string? TryGetWorktreePath(string? repositoryPath, string ticketId)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return null;
        }

        string? worktreesRoot = TryGetWorktreesDirectory(repositoryPath);
        if (string.IsNullOrWhiteSpace(worktreesRoot))
        {
            return null;
        }

        return Path.Combine(worktreesRoot, SanitizeTicketId(ticketId));
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

    // ── Reviewing and applying ticket worktree changes ─────────────

    /// <summary>
    /// Lists every file the agent changed inside a ticket worktree: commits made on
    /// the <c>agent/&lt;ticket-id&gt;</c> branch plus uncommitted working-directory
    /// changes, each compared against the commit the worktree started from. Paths
    /// under <c>.kanecode/</c> are IDE state and are never reported as agent work.
    /// Returns an empty list when the worktree has no changes.
    /// </summary>
    public IReadOnlyList<TicketWorktreeChange> GetWorktreeChanges(string repositoryPath, string worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        using Repository worktreeRepository = new(worktreePath);
        Commit? agentTip = worktreeRepository.Head?.Tip;
        ObjectId? baseCommitId = ResolveBaseCommitId(repositoryPath, worktreeRepository, agentTip);
        Commit? baseCommit = baseCommitId is null ? null : worktreeRepository.Lookup<Commit>(baseCommitId);

        // Every path the agent could have changed, from commits and from the
        // working directory. The working directory decides the final state.
        HashSet<string> candidatePaths = new(StringComparer.OrdinalIgnoreCase);

        if (baseCommit is not null && agentTip is not null && !baseCommit.Id.Equals(agentTip.Id))
        {
            TreeChanges committed = worktreeRepository.Diff.Compare<TreeChanges>(baseCommit.Tree, agentTip.Tree);
            foreach (TreeEntryChanges change in committed)
            {
                if (!string.IsNullOrWhiteSpace(change.Path))
                {
                    AddCandidatePath(candidatePaths, change.Path);
                }

                if (!string.IsNullOrWhiteSpace(change.OldPath))
                {
                    AddCandidatePath(candidatePaths, change.OldPath);
                }
            }
        }

        RepositoryStatus status = worktreeRepository.RetrieveStatus();
        foreach (StatusEntry entry in status)
        {
            if (entry.State is not FileStatus.Unaltered and not FileStatus.Ignored)
            {
                AddCandidatePath(candidatePaths, entry.FilePath);
            }
        }

        // Files present in the base commit, used to tell additions from modifications.
        HashSet<string> basePaths = [];
        if (baseCommit is not null)
        {
            CollectTreePaths(baseCommit.Tree, string.Empty, basePaths);
        }

        List<TicketWorktreeChange> changes = [];
        foreach (string relativePath in candidatePaths)
        {
            string sourcePath = ResolveSafePath(worktreePath, relativePath);
            bool exists = File.Exists(sourcePath);

            TicketWorktreeChangeKind kind = exists
                ? (basePaths.Contains(relativePath) ? TicketWorktreeChangeKind.Modified : TicketWorktreeChangeKind.Added)
                : TicketWorktreeChangeKind.Deleted;

            changes.Add(new TicketWorktreeChange(relativePath, kind));
        }

        return changes
            .OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Applies every change from a ticket worktree to the main IDE worktree: new and
    /// modified files are copied over, deleted files are removed. Nothing is staged or
    /// committed — the changes land as ordinary working-directory changes so the user
    /// can review them. Returns the number of files applied.
    /// </summary>
    public int ApplyWorktreeChangesToWorkspace(string repositoryPath, string worktreePath)
    {
        IReadOnlyList<TicketWorktreeChange> changes = GetWorktreeChanges(repositoryPath, worktreePath);

        int applied = 0;
        foreach (TicketWorktreeChange change in changes)
        {
            string targetPath = ResolveSafePath(repositoryPath, change.RelativePath);

            if (change.Kind == TicketWorktreeChangeKind.Deleted)
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    applied++;
                }

                continue;
            }

            string sourcePath = ResolveSafePath(worktreePath, change.RelativePath);
            if (!File.Exists(sourcePath))
            {
                // The working directory no longer has the file (e.g. it was deleted
                // after the diff was computed); treat it as deleted in the target.
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    applied++;
                }

                continue;
            }

            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Applies every change from a ticket worktree to the main IDE worktree, stages
    /// exactly those files, and commits them with <paramref name="message"/> on the
    /// main worktree's current branch. Returns the number of files committed.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the commit message is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when Git identity is not configured.</exception>
    public int CommitWorktreeChanges(string repositoryPath, string worktreePath, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        IReadOnlyList<TicketWorktreeChange> changes = GetWorktreeChanges(repositoryPath, worktreePath);
        int applied = ApplyWorktreeChangesToWorkspace(repositoryPath, worktreePath);
        if (applied == 0)
        {
            return 0;
        }

        using Repository mainRepository = new(repositoryPath);

        // Stage exactly the agent's changed files. Staging "*" would also pick up
        // unrelated working-directory changes and the untracked .kanecode folder.
        foreach (TicketWorktreeChange change in changes)
        {
            Commands.Stage(mainRepository, change.RelativePath);
        }

        Signature? signature = mainRepository.Config.BuildSignature(DateTimeOffset.Now)
            ?? throw new InvalidOperationException("Git user name and email must be configured before committing.");

        mainRepository.Commit(message.Trim(), signature, signature);
        return applied;
    }

    /// <summary>
    /// Finds the SHA of the commit the agent branch diverged from — the merge-base of
    /// the main worktree's HEAD and the worktree's HEAD — so only the agent's own
    /// changes are reported even when the main branch moved on while the agent was
    /// working. Returns an <see cref="ObjectId"/> (not a commit) so callers resolve it
    /// through the worktree repository, whose handle stays alive for the whole diff.
    /// </summary>
    private static ObjectId? ResolveBaseCommitId(string repositoryPath, Repository worktreeRepository, Commit? agentTip)
    {
        ObjectId? mainTipId;
        using (Repository mainRepository = new(repositoryPath))
        {
            mainTipId = mainRepository.Head?.Tip?.Id;
        }

        if (mainTipId is null)
        {
            return null;
        }

        if (agentTip is null || agentTip.Id.Equals(mainTipId))
        {
            return mainTipId;
        }

        // Both commits live in the shared object store, so the worktree repository can
        // resolve the main branch tip and compute the merge-base without needing the
        // (now closed) main repository handle.
        Commit? mainTip = worktreeRepository.Lookup<Commit>(mainTipId);
        if (mainTip is null)
        {
            return mainTipId;
        }

        return worktreeRepository.ObjectDatabase.FindMergeBase(mainTip, agentTip)?.Id ?? mainTipId;
    }

    /// <summary>Adds a repo-relative path to the candidate set unless it is IDE state.</summary>
    private static void AddCandidatePath(HashSet<string> paths, string relativePath)
    {
        if (relativePath.StartsWith(KaneCodeFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        paths.Add(relativePath);
    }

    /// <summary>
    /// Recursively collects every blob path under a tree, used to tell added files
    /// from modified files.
    /// </summary>
    private static void CollectTreePaths(Tree? tree, string prefix, HashSet<string> paths)
    {
        if (tree is null)
        {
            return;
        }

        foreach (TreeEntry entry in tree)
        {
            string path = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;

            if (entry.TargetType == TreeEntryTargetType.Tree && entry.Target is Tree subTree)
            {
                CollectTreePaths(subTree, path, paths);
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob)
            {
                paths.Add(path);
            }
        }
    }

    /// <summary>
    /// Resolves a repo-relative path against a root, refusing to escape it so a
    /// malformed path can never read or write outside the repository/worktree.
    /// </summary>
    private static string ResolveSafePath(string root, string relativePath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to access a path outside the repository: {relativePath}");
        }

        return combined;
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
