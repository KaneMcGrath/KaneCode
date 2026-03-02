using LibGit2Sharp;

namespace KaneCode.Models;

/// <summary>
/// Represents a single file status entry from a Git repository snapshot.
/// </summary>
internal sealed record GitFileStatusEntry(string FilePath, FileStatus Status);
