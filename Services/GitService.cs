using System.IO;
using KaneCode.Models;
using LibGit2Sharp;

namespace KaneCode.Services;

/// <summary>
/// Provides basic repository detection and open/close lifecycle operations.
/// </summary>
internal sealed class GitService : IDisposable
{
    private Repository? _repository;
    private readonly Dictionary<string, FileStatus> _lastStatusByPath = new(StringComparer.OrdinalIgnoreCase);

    internal event Action<IReadOnlyList<GitFileStatusEntry>>? StatusChanged;

    /// <summary>
    /// Gets a value indicating whether a repository is currently open.
    /// </summary>
    internal bool IsRepositoryOpen => _repository is not null;

    /// <summary>
    /// Gets the opened repository working directory path.
    /// </summary>
    internal string? RepositoryWorkingDirectory => _repository?.Info.WorkingDirectory;

    /// <summary>
    /// Gets the currently checked out branch friendly name.
    /// </summary>
    internal string? CurrentBranchName => _repository?.Head?.FriendlyName;

    /// <summary>
    /// Attempts to detect a Git repository from the provided file or directory path.
    /// </summary>
    /// <param name="path">File system path used as the detection starting point.</param>
    /// <param name="repositoryPath">Detected repository path when found.</param>
    /// <returns><see langword="true"/> if a repository is detected; otherwise <see langword="false"/>.</returns>
    internal bool TryDetectRepository(string path, out string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A valid path is required.", nameof(path));
        }

        var searchPath = ResolveSearchPath(path);
        if (string.IsNullOrEmpty(searchPath))
        {
            repositoryPath = null;
            return false;
        }

        repositoryPath = Repository.Discover(searchPath);
        return !string.IsNullOrWhiteSpace(repositoryPath);
    }

    /// <summary>
    /// Opens a Git repository discovered from the provided path.
    /// </summary>
    /// <param name="path">File system path used as the repository discovery starting point.</param>
    /// <returns><see langword="true"/> if a repository was opened; otherwise <see langword="false"/>.</returns>
    internal bool TryOpenRepository(string path)
    {
        if (!TryDetectRepository(path, out var repositoryPath) || string.IsNullOrWhiteSpace(repositoryPath))
        {
            CloseRepository();
            return false;
        }

        var repository = new Repository(repositoryPath);
        _repository?.Dispose();
        _repository = repository;
        RefreshStatus();

        return true;
    }

    /// <summary>
    /// Creates a new non-bare Git repository in the given directory and opens it.
    /// </summary>
    internal bool TryInitializeRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A valid path is required.", nameof(path));
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        var gitDir = Repository.Init(path, isBare: false);
        if (string.IsNullOrWhiteSpace(gitDir))
        {
            return false;
        }

        var repository = new Repository(gitDir);
        _repository?.Dispose();
        _repository = repository;
        _lastStatusByPath.Clear();
        StatusChanged?.Invoke([]);
        return true;
    }

    /// <summary>
    /// Returns the current per-file repository status for changed and untracked files.
    /// </summary>
    internal IReadOnlyList<GitFileStatusEntry> GetStatus()
    {
        if (_repository is null)
        {
            return [];
        }

        var status = _repository.RetrieveStatus();
        return BuildStatusSnapshot(status);
    }

    /// <summary>
    /// Stages the file at the given repository-relative path.
    /// </summary>
    internal void StageFile(string relativePath)
    {
        if (_repository is null) return;
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A valid path is required.", nameof(relativePath));

        Commands.Stage(_repository, NormalizePath(relativePath));
        RefreshStatus();
    }

    /// <summary>
    /// Stages all unstaged and untracked files.
    /// </summary>
    internal void StageAll()
    {
        if (_repository is null) return;
        Commands.Stage(_repository, "*");
        RefreshStatus();
    }

    /// <summary>
    /// Removes the file at the given repository-relative path from the staging index.
    /// </summary>
    internal void UnstageFile(string relativePath)
    {
        if (_repository is null) return;
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A valid path is required.", nameof(relativePath));

        Commands.Unstage(_repository, NormalizePath(relativePath));
        RefreshStatus();
    }

    /// <summary>
    /// Removes all staged files from the staging index.
    /// </summary>
    internal void UnstageAll()
    {
        if (_repository is null) return;
        Commands.Unstage(_repository, "*");
        RefreshStatus();
    }

    /// <summary>
    /// Discards working-directory changes to the file at the given repository-relative path.
    /// Untracked new files are deleted from disk; tracked modified or deleted files are
    /// restored from HEAD.
    /// </summary>
    internal void DiscardFile(string relativePath)
    {
        if (_repository is null) return;
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A valid path is required.", nameof(relativePath));

        var normalizedPath = NormalizePath(relativePath);

        _lastStatusByPath.TryGetValue(normalizedPath, out var fileStatus);

        if (fileStatus.HasFlag(FileStatus.NewInWorkdir))
        {
            // Untracked file — delete from disk.
            var workDir = _repository.Info.WorkingDirectory;
            var fullPath = Path.GetFullPath(Path.Combine(workDir,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        else
        {
            // Tracked modified/deleted file — restore from HEAD.
            _repository.CheckoutPaths("HEAD", [normalizedPath],
                new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        }

        RefreshStatus();
    }

    /// <summary>
    /// Refreshes repository status and raises <see cref="StatusChanged"/> when it differs from the previous snapshot.
    /// </summary>
    internal IReadOnlyList<GitFileStatusEntry> RefreshStatus()
    {
        var snapshot = GetStatus();
        if (!HasStatusChanged(snapshot))
        {
            return snapshot;
        }

        _lastStatusByPath.Clear();
        foreach (var item in snapshot)
        {
            _lastStatusByPath[item.FilePath] = item.Status;
        }

        StatusChanged?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Closes the currently opened repository.
    /// </summary>
    internal void CloseRepository()
    {
        var hadStatus = _lastStatusByPath.Count > 0;

        _repository?.Dispose();
        _repository = null;
        _lastStatusByPath.Clear();

        if (hadStatus)
        {
            StatusChanged?.Invoke([]);
        }
    }

    public void Dispose()
    {
        CloseRepository();
    }

    private static string? ResolveSearchPath(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        if (File.Exists(path))
        {
            return Path.GetDirectoryName(path);
        }

        return Path.GetDirectoryName(path);
    }

    private static List<GitFileStatusEntry> BuildStatusSnapshot(RepositoryStatus status)
    {
        return status
            .Where(entry => entry.State != FileStatus.Unaltered && entry.State != FileStatus.Ignored)
            .Select(entry => new GitFileStatusEntry(entry.FilePath, entry.State))
            .OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool HasStatusChanged(IReadOnlyList<GitFileStatusEntry> snapshot)
    {
        if (_lastStatusByPath.Count != snapshot.Count)
        {
            return true;
        }

        foreach (var item in snapshot)
        {
            if (!_lastStatusByPath.TryGetValue(item.FilePath, out var status) || status != item.Status)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Normalizes path separators to the forward-slash form expected by LibGit2Sharp.</summary>
    private static string NormalizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');
}
