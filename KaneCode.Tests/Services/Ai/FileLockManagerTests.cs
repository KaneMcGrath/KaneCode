using KaneCode.Services.Ai.Agents;
using System.IO;

namespace KaneCode.Tests.Services.Ai;

public sealed class FileLockManagerTests
{
    private static string TestFilePath =>
        Path.Combine(Path.GetTempPath(), $"kane_lock_test_{Guid.NewGuid():N}.txt");

    [Fact]
    public async Task WhenLockIsFreeThenWaitForWriteLockAcquiresImmediately()
    {
        FileLockManager manager = new();
        string path = TestFilePath;

        FileLockResult result = await manager.WaitForWriteLockAsync(
            path, "agent-a", TimeSpan.FromSeconds(5));

        Assert.True(result.Acquired);
        Assert.Null(result.ConflictingAgentId);
    }

    [Fact]
    public async Task WhenLockIsOwnedBySameAgentThenWaitForWriteLockAcquiresImmediately()
    {
        FileLockManager manager = new();
        string path = TestFilePath;
        Assert.True(manager.TryAcquireWriteLock(path, "agent-a"));

        // Re-entrant acquisition from the same agent must not block.
        FileLockResult result = await manager.WaitForWriteLockAsync(
            path, "agent-a", TimeSpan.FromSeconds(5));

        Assert.True(result.Acquired);
    }

    [Fact]
    public async Task WhenLockIsHeldThenWaitForWriteLockDelaysUntilReleased()
    {
        FileLockManager manager = new();
        string path = TestFilePath;
        Assert.True(manager.TryAcquireWriteLock(path, "agent-a"));

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Task<FileLockResult> waiting = manager.WaitForWriteLockAsync(
            path, "agent-b", TimeSpan.FromSeconds(10));

        // Give the waiter a moment to start polling, then release the lock.
        await Task.Delay(200);
        manager.ReleaseAll("agent-a");

        FileLockResult result = await waiting;

        // The waiter must have blocked until the holder released the lock.
        Assert.True(result.Acquired);
        Assert.True(DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMilliseconds(100));

        IReadOnlyDictionary<string, FileLockInfo> snapshot = manager.GetSnapshot();
        Assert.Contains(snapshot.Values, info => info.AgentId == "agent-b");
    }

    [Fact]
    public async Task WhenLockIsNeverReleasedThenWaitForWriteLockTimesOutWithConflict()
    {
        FileLockManager manager = new();
        string path = TestFilePath;
        Assert.True(manager.TryAcquireWriteLock(path, "agent-a"));

        FileLockResult result = await manager.WaitForWriteLockAsync(
            path, "agent-b", TimeSpan.FromMilliseconds(250));

        Assert.False(result.Acquired);
        Assert.Equal("agent-a", result.ConflictingAgentId);
        Assert.NotNull(result.ConflictingLockAcquiredAt);
    }

    [Fact]
    public async Task WhenCancelledThenWaitForWriteLockPropagatesCancellation()
    {
        FileLockManager manager = new();
        string path = TestFilePath;
        Assert.True(manager.TryAcquireWriteLock(path, "agent-a"));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.WaitForWriteLockAsync(path, "agent-b", TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public async Task WhenCancelledWhileWaitingThenWaitForWriteLockPropagatesCancellation()
    {
        FileLockManager manager = new();
        string path = TestFilePath;
        Assert.True(manager.TryAcquireWriteLock(path, "agent-a"));

        using CancellationTokenSource cts = new();
        Task<FileLockResult> waiting = manager.WaitForWriteLockAsync(
            path, "agent-b", TimeSpan.FromSeconds(30), cts.Token);

        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task WhenTimeoutIsNegativeThenWaitForWriteLockThrows()
    {
        FileLockManager manager = new();
        string path = TestFilePath;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            manager.WaitForWriteLockAsync(path, "agent-a", TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task WhenLockIsReleasedThenNextWaiterAcquiresAndLaterWaitersQueue()
    {
        FileLockManager manager = new();
        string path = TestFilePath;
        Assert.True(manager.TryAcquireWriteLock(path, "agent-a"));

        // agent-b queues behind agent-a and acquires once it releases.
        Task<FileLockResult> secondWaiter = manager.WaitForWriteLockAsync(
            path, "agent-b", TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        manager.ReleaseAll("agent-a");

        FileLockResult second = await secondWaiter;
        Assert.True(second.Acquired);

        // agent-b now holds the lock; agent-c must queue behind it too.
        Task<FileLockResult> thirdWaiter = manager.WaitForWriteLockAsync(
            path, "agent-c", TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        manager.ReleaseAll("agent-b");

        FileLockResult third = await thirdWaiter;
        Assert.True(third.Acquired);
    }
}
