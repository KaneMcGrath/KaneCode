using System.Collections.Concurrent;
using System.IO;

namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// Tracks file locks across agents to prevent conflicting simultaneous edits.
///
/// Before a write tool executes, the agent must acquire a lock on the target file.
/// If another agent holds a lock on the same file, the tool receives a conflict
/// result so the model can retry or take alternative action.
///
/// Locks are released when the agent completes its tool loop or explicitly releases them.
/// </summary>
internal sealed class FileLockManager
{
    /// <summary>
    /// Maps file paths (normalized, case-insensitive) to the agent ID that currently holds the lock.
    /// </summary>
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks per-agent lock sets for fast release-all-on-completion.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _agentLocks = new(StringComparer.Ordinal);

    private sealed class LockEntry
    {
        public string AgentId { get; init; } = string.Empty;

        public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// The type of lock: Write (exclusive) or Read (shared).
        /// Currently only Write locks are used, but Read is reserved for future use.
        /// </summary>
        public FileLockType LockType { get; init; } = FileLockType.Write;
    }

    /// <summary>
    /// Attempts to acquire a write lock on the given file path for the specified agent.
    /// Returns true if the lock was acquired, false if another agent already holds a lock.
    /// </summary>
    public bool TryAcquireWriteLock(string filePath, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        string normalizedPath = NormalizePath(filePath);

        LockEntry newEntry = new()
        {
            AgentId = agentId,
            LockType = FileLockType.Write,
            AcquiredAt = DateTimeOffset.UtcNow
        };

        LockEntry? added = _locks.GetOrAdd(normalizedPath, newEntry);

        // If we added it (key was new), or the existing lock is ours, we have the lock
        if (ReferenceEquals(added, newEntry) || string.Equals(added.AgentId, agentId, StringComparison.Ordinal))
        {
            _agentLocks.AddOrUpdate(
                agentId,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedPath },
                (_, set) =>
                {
                    set.Add(normalizedPath);
                    return set;
                });

            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to acquire a write lock, and returns a <see cref="FileLockResult"/>
    /// describing success or the conflict details.
    /// </summary>
    public FileLockResult TryAcquireWriteLockWithResult(string filePath, string agentId)
    {
        if (TryAcquireWriteLock(filePath, agentId))
        {
            return FileLockResult.Success();
        }

        if (_locks.TryGetValue(NormalizePath(filePath), out LockEntry? existing))
        {
            return FileLockResult.Conflict(existing.AgentId, existing.AcquiredAt);
        }

        // Should not happen, but be safe
        return FileLockResult.Conflict("unknown", DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Releases all locks held by the given agent.
    /// Called when the agent completes its run loop.
    /// </summary>
    public void ReleaseAll(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        if (_agentLocks.TryRemove(agentId, out HashSet<string>? paths))
        {
            foreach (string path in paths)
            {
                _locks.TryRemove(path, out _);
            }
        }
    }

    /// <summary>
    /// Releases a specific lock held by the given agent on the given file.
    /// </summary>
    public void Release(string filePath, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        string normalizedPath = NormalizePath(filePath);

        if (_locks.TryGetValue(normalizedPath, out LockEntry? existing) &&
            string.Equals(existing.AgentId, agentId, StringComparison.Ordinal))
        {
            _locks.TryRemove(normalizedPath, out _);

            if (_agentLocks.TryGetValue(agentId, out HashSet<string>? paths))
            {
                paths.Remove(normalizedPath);
            }
        }
    }

    /// <summary>
    /// Returns true if the given file is locked by any agent other than <paramref name="agentId"/>.
    /// </summary>
    public bool IsLockedByOther(string filePath, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        string normalizedPath = NormalizePath(filePath);

        if (_locks.TryGetValue(normalizedPath, out LockEntry? existing))
        {
            return !string.Equals(existing.AgentId, agentId, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Returns a snapshot of all current locks.
    /// </summary>
    public IReadOnlyDictionary<string, FileLockInfo> GetSnapshot()
    {
        Dictionary<string, FileLockInfo> snapshot = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, LockEntry> kvp in _locks)
        {
            snapshot[kvp.Key] = new FileLockInfo(
                kvp.Value.AgentId,
                kvp.Value.LockType,
                kvp.Value.AcquiredAt);
        }

        return snapshot;
    }

    private static string NormalizePath(string filePath)
    {
        return Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

/// <summary>
/// The type of file lock.
/// </summary>
internal enum FileLockType
{
    /// <summary>Exclusive lock for write operations.</summary>
    Write,

    /// <summary>Shared lock for read operations (reserved for future use).</summary>
    Read
}

/// <summary>
/// Information about a current file lock.
/// </summary>
internal sealed record FileLockInfo(string AgentId, FileLockType LockType, DateTimeOffset AcquiredAt);

/// <summary>
/// The result of attempting to acquire a file lock.
/// </summary>
internal sealed record FileLockResult
{
    /// <summary>Whether the lock was acquired.</summary>
    public bool Acquired { get; init; }

    /// <summary>The ID of the agent holding the conflicting lock, if any.</summary>
    public string? ConflictingAgentId { get; init; }

    /// <summary>When the conflicting lock was acquired.</summary>
    public DateTimeOffset? ConflictingLockAcquiredAt { get; init; }

    public static FileLockResult Success() => new() { Acquired = true };

    public static FileLockResult Conflict(string agentId, DateTimeOffset acquiredAt) => new()
    {
        Acquired = false,
        ConflictingAgentId = agentId,
        ConflictingLockAcquiredAt = acquiredAt
    };
}
