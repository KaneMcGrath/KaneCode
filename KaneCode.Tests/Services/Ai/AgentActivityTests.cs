using KaneCode.Models;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Agents;
using KaneCode.Services.Ai.Modes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

/// <summary>
/// Covers the live progress notifications an agent broadcasts while it runs. These are
/// what let an observer follow an agent it did not dispatch itself — the chat panel
/// rendering a ticket agent's session, for example.
/// </summary>
public sealed class AgentActivityTests
{
    /// <summary>A provider that replays a fixed token script for every request.</summary>
    private sealed class ScriptedProvider : IAiProvider
    {
        private readonly IReadOnlyList<AiStreamToken> _tokens;

        public ScriptedProvider(IReadOnlyList<AiStreamToken> tokens)
        {
            _tokens = tokens;
        }

        public string DisplayName => "Scripted";
        public string ProviderId => "scripted";
        public bool SupportsImages => false;
        public bool IsConfigured => true;
        public IReadOnlyList<string> AvailableModels => ["scripted-model"];

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AvailableModels);
        }

        public async IAsyncEnumerable<AiStreamToken> StreamCompletionAsync(
            IReadOnlyList<AiChatMessage> messages,
            string model,
            JsonElement tools = default,
            bool streamResponse = true,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (AiStreamToken token in _tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return token;
                await Task.Yield();
            }
        }
    }

    /// <summary>A provider that always fails, to exercise the error exit path.</summary>
    private sealed class ThrowingProvider : IAiProvider
    {
        public string DisplayName => "Throwing";
        public string ProviderId => "throwing";
        public bool SupportsImages => false;
        public bool IsConfigured => true;
        public IReadOnlyList<string> AvailableModels => ["throwing-model"];

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AvailableModels);
        }

        public async IAsyncEnumerable<AiStreamToken> StreamCompletionAsync(
            IReadOnlyList<AiChatMessage> messages,
            string model,
            JsonElement tools = default,
            bool streamResponse = true,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("provider exploded");
#pragma warning disable CS0162 // Unreachable — required to make this an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }

    private static (Agent Agent, AgentOrchestrator Orchestrator, AgentToolRegistry ToolRegistry) CreateTicketAgent(
        IAiProvider provider)
    {
        AgentToolRegistry toolRegistry = new();
        AiProviderRegistry providerRegistry = new();
        AiChatModeRegistry modeRegistry = new();
        AgentOrchestrator orchestrator = new(toolRegistry, providerRegistry, modeRegistry);

        Agent agent = new(
            "ticket_agent_1",
            AgentRole.Ticket,
            "Ticket: Do the thing",
            provider,
            "scripted-model",
            new NoToolsMode());

        return (agent, orchestrator, toolRegistry);
    }

    private static Task<AgentRunResult> RunAsync(
        Agent agent,
        AgentOrchestrator orchestrator,
        AgentToolRegistry toolRegistry,
        CancellationToken cancellationToken = default)
    {
        return agent.RunAsync(
            "Do the thing",
            default,
            toolRegistry,
            orchestrator.FileLockManager,
            orchestrator,
            maxIterations: 5,
            cancellationToken);
    }

    [Fact]
    public async Task RunAsync_RaisesTokenStreamedForEachStreamedToken()
    {
        (Agent agent, AgentOrchestrator orchestrator, AgentToolRegistry toolRegistry) = CreateTicketAgent(
            new ScriptedProvider(
            [
                new AiStreamToken(AiStreamTokenType.Reasoning, "thinking"),
                new AiStreamToken(AiStreamTokenType.Content, "Hello "),
                new AiStreamToken(AiStreamTokenType.Content, "world")
            ]));

        List<AiStreamToken> observed = [];
        agent.TokenStreamed += (_, e) => observed.Add(e.Token);

        await RunAsync(agent, orchestrator, toolRegistry);

        Assert.Equal(3, observed.Count);
        Assert.Equal(AiStreamTokenType.Reasoning, observed[0].Type);
        Assert.Equal("Hello ", observed[1].Text);
        Assert.Equal("world", observed[2].Text);
    }

    [Fact]
    public async Task RunAsync_RaisesIterationStartedAndRunCompleted()
    {
        (Agent agent, AgentOrchestrator orchestrator, AgentToolRegistry toolRegistry) = CreateTicketAgent(
            new ScriptedProvider([new AiStreamToken(AiStreamTokenType.Content, "done")]));

        List<AgentActivityKind> observed = [];
        agent.Activity += (_, e) => observed.Add(e.Kind);

        await RunAsync(agent, orchestrator, toolRegistry);

        Assert.Contains(AgentActivityKind.IterationStarted, observed);
        Assert.Contains(AgentActivityKind.MessagesChanged, observed);
        Assert.Equal(AgentActivityKind.RunCompleted, observed[^1]);
    }

    [Fact]
    public async Task RunAsync_RaisesMessagesChangedAfterTheAssistantMessageIsRecorded()
    {
        (Agent agent, AgentOrchestrator orchestrator, AgentToolRegistry toolRegistry) = CreateTicketAgent(
            new ScriptedProvider([new AiStreamToken(AiStreamTokenType.Content, "done")]));

        // The observer must be able to read the completed message from the event, since
        // that is how the chat panel re-renders an agent's session as it progresses.
        int messagesAtLastChange = 0;
        agent.Activity += (sender, e) =>
        {
            if (e.Kind == AgentActivityKind.MessagesChanged && sender is IAgent observedAgent)
            {
                messagesAtLastChange = observedAgent.Messages.Count;
            }
        };

        await RunAsync(agent, orchestrator, toolRegistry);

        // The user task plus the assistant reply.
        Assert.Equal(2, messagesAtLastChange);
        Assert.Equal(AiChatRole.Assistant, agent.Messages[^1].Role);
    }

    [Fact]
    public async Task RunAsync_RaisesRunCompletedWhenTheProviderFails()
    {
        (Agent agent, AgentOrchestrator orchestrator, AgentToolRegistry toolRegistry) = CreateTicketAgent(
            new ThrowingProvider());

        bool completed = false;
        agent.Activity += (_, e) => completed |= e.Kind == AgentActivityKind.RunCompleted;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(agent, orchestrator, toolRegistry));

        // Without this the agent view would stay frozen on the last streamed token.
        Assert.True(completed);
    }

    [Fact]
    public async Task RunAsync_RaisesRunCompletedWhenCancelled()
    {
        (Agent agent, AgentOrchestrator orchestrator, AgentToolRegistry toolRegistry) = CreateTicketAgent(
            new ScriptedProvider([new AiStreamToken(AiStreamTokenType.Content, "partial")]));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        bool completed = false;
        agent.Activity += (_, e) => completed |= e.Kind == AgentActivityKind.RunCompleted;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(agent, orchestrator, toolRegistry, cts.Token));

        Assert.True(completed);
    }

    [Fact]
    public async Task RunAsync_ContinuesWhenAnObserverThrows()
    {
        (Agent agent, AgentOrchestrator orchestrator, AgentToolRegistry toolRegistry) = CreateTicketAgent(
            new ScriptedProvider([new AiStreamToken(AiStreamTokenType.Content, "done")]));

        agent.Activity += (_, _) => throw new InvalidOperationException("observer bug");
        agent.TokenStreamed += (_, _) => throw new InvalidOperationException("observer bug");

        AgentRunResult result = await RunAsync(agent, orchestrator, toolRegistry);

        // A broken UI observer must never take the agent's run down with it.
        Assert.True(result.Success);
        Assert.Equal("done", result.Summary);
    }

    [Fact]
    public void Messages_ReturnsASnapshotThatIsSafeToEnumerateWhileTheAgentAppends()
    {
        (Agent agent, _, _) = CreateTicketAgent(new ScriptedProvider([]));

        agent.AddMessage(new AiChatMessage(AiChatRole.User, "first"));
        IReadOnlyList<AiChatMessage> snapshot = agent.Messages;
        agent.AddMessage(new AiChatMessage(AiChatRole.Assistant, "second"));

        Assert.Single(snapshot);
        Assert.Equal(2, agent.MessageCount);
    }
}
