using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Agents;
using KaneCode.Services.Ai.Modes;
using KaneCode.Services.Tickets;
using LibGit2Sharp;
using System.IO;

namespace KaneCode.Tests.Services.Tickets;

public sealed class TicketSystemTests
{
    private static string CreateTempProjectRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "kanecode-tickets-system-tests", Guid.NewGuid().ToString("N"));
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
    /// Captures the first non-null dispatch issue. The system raises a "cleared"
    /// event (null reason) when it starts, so the capture ignores those.
    /// </summary>
    private sealed class DispatchIssueCapture
    {
        private readonly TaskCompletionSource<string> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> Reason => _tcs.Task;

        public void Handle(object? sender, TicketDispatchIssueEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Reason))
            {
                _tcs.TrySetResult(e.Reason);
            }
        }
    }

    [Fact]
    public async Task Start_WhenNoProviderConfigured_RaisesDispatchIssueWithActionableReason()
    {
        string root = CreateTempProjectRoot();
        try
        {
            AgentToolRegistry toolRegistry = new();
            AiProviderRegistry providerRegistry = new();
            AiChatModeRegistry modeRegistry = new();
            modeRegistry.Register(new AgentMode());

            TicketFileStore store = new(() => root);
            store.CreateTicket("My Ticket", "Do the thing");

            AgentOrchestrator orchestrator = new(toolRegistry, providerRegistry, modeRegistry);

            using TicketSystem system = new(
                store,
                toolRegistry,
                providerRegistry,
                modeRegistry,
                orchestrator,
                () => root,
                () => null,
                () => providerRegistry.ActiveProvider,
                () => null,
                () => modeRegistry.Get("agent"));

            DispatchIssueCapture capture = new();
            system.DispatchIssue += capture.Handle;

            system.Start();

            string reason = await capture.Reason.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("provider", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Start_WhenActiveProviderIsNotConfigured_RaisesDispatchIssueNamingProvider()
    {
        string root = CreateTempProjectRoot();
        try
        {
            AgentToolRegistry toolRegistry = new();
            AiProviderRegistry providerRegistry = new();
            AiChatModeRegistry modeRegistry = new();
            modeRegistry.Register(new AgentMode());

            // A provider instance that exists but has no API key/endpoint configured.
            AiProviderSettings settings = new()
            {
                ProviderId = "v1chatcompletions",
                Label = "Unconfigured Chat"
            };
            IAiProvider unconfigured = new V1ChatCompletionsProvider(settings);
            providerRegistry.SetActiveProvider(unconfigured);

            TicketFileStore store = new(() => root);
            store.CreateTicket("My Ticket", "Do the thing");

            AgentOrchestrator orchestrator = new(toolRegistry, providerRegistry, modeRegistry);

            using TicketSystem system = new(
                store,
                toolRegistry,
                providerRegistry,
                modeRegistry,
                orchestrator,
                () => root,
                () => null,
                () => providerRegistry.ActiveProvider,
                () => null,
                () => modeRegistry.Get("agent"));

            DispatchIssueCapture capture = new();
            system.DispatchIssue += capture.Handle;

            system.Start();

            string reason = await capture.Reason.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("not configured", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }

    [Fact]
    public void CreateWorktree_WhenRepositoryHasNoCommits_ThrowsActionableMessage()
    {
        string root = CreateTempProjectRoot();
        try
        {
            Repository.Init(root);

            TicketWorktreeManager manager = new();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => manager.CreateWorktree(root, "My Ticket"));

            Assert.Contains("no commits", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateWorktree_WhenRepositoryHasCommits_CreatesAndRemovesWorktree()
    {
        string root = CreateTempProjectRoot();
        try
        {
            Repository.Init(root);
            File.WriteAllText(Path.Combine(root, "file.txt"), "hello");

            using (Repository repo = new(root))
            {
                Commands.Stage(repo, "*");
                Signature author = new("Test", "test@example.com", DateTimeOffset.Now);
                repo.Commit("initial commit", author, author);
            }

            TicketWorktreeManager manager = new();
            string? worktree = manager.CreateWorktree(root, "My Ticket");

            Assert.NotNull(worktree);
            Assert.True(Directory.Exists(worktree));
            Assert.Contains(Path.Combine(".kanecode", "worktrees"), worktree, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(worktree, "file.txt")));

            manager.RemoveWorktree(root, "My Ticket");
            Assert.False(Directory.Exists(worktree));
        }
        finally
        {
            ForceDeleteDirectory(root);
        }
    }
}
