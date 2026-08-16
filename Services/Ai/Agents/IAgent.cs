using System.Text.Json;

namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// A self-contained AI agent that can run a tool-calling loop with its own
/// provider, model, mode, and message history.
///
/// Agents are organized in a tree: the root agent (the main AI chat) can spawn
/// sub-agents to handle delegated tasks. Communication flows between a parent
/// and its children by default.
/// </summary>
internal interface IAgent
{
    /// <summary>
    /// Raised while the agent runs, whenever it starts an iteration, appends
    /// messages, or finishes its run. Multicast, so any observer can follow an
    /// agent it did not dispatch itself (for example the chat panel rendering a
    /// ticket agent's session). Raised on the agent's background run thread —
    /// subscribers must marshal to their own thread.
    /// </summary>
    event EventHandler<AgentActivityEventArgs>? Activity;

    /// <summary>
    /// Raised for every streaming token the agent receives from its provider,
    /// so observers can render the response as it is produced. Raised on the
    /// agent's background run thread — subscribers must marshal to their own thread.
    /// </summary>
    event EventHandler<AgentTokenEventArgs>? TokenStreamed;

    /// <summary>
    /// Unique identifier for this agent within the orchestrator.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The role of this agent in the hierarchy.
    /// </summary>
    AgentRole Role { get; }

    /// <summary>
    /// Human-readable display name for debugging/logging.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// The provider this agent uses for completions. Can be different
    /// from other agents in the same orchestrator.
    /// </summary>
    IAiProvider Provider { get; }

    /// <summary>
    /// The model identifier used by this agent.
    /// </summary>
    string Model { get; }

    /// <summary>
    /// The chat mode controlling which tools this agent can use.
    /// </summary>
    IAiChatMode Mode { get; }

    /// <summary>
    /// The effective system prompt for this agent.
    /// Includes the mode's prompt plus any agent-specific instructions.
    /// </summary>
    string? SystemPrompt { get; }

    /// <summary>
    /// The ID of the parent agent, or null if this is the root agent.
    /// </summary>
    string? ParentId { get; }

    /// <summary>
    /// The IDs of child agents spawned by this agent.
    /// </summary>
    IReadOnlySet<string> ChildIds { get; }

    /// <summary>
    /// A snapshot of the messages in this agent's conversation history.
    /// Each read returns a new copy so observers on other threads can enumerate
    /// it safely while the agent keeps appending on its run thread.
    /// </summary>
    IReadOnlyList<AiChatMessage> Messages { get; }

    /// <summary>
    /// The number of messages in this agent's conversation history. Cheaper than
    /// <see cref="Messages"/> when only the count is needed, since it does not copy.
    /// </summary>
    int MessageCount { get; }

    /// <summary>
    /// Runs the agent's tool-calling loop with the given user task.
    /// The agent will iterate: send messages → receive response → execute tools → repeat,
    /// until the task is complete or the iteration limit is reached.
    /// </summary>
    /// <param name="task">The task description for this agent to work on.</param>
    /// <param name="toolsDef">Pre-serialized tool definitions to send.</param>
    /// <param name="toolRegistry">The tool registry for executing tool calls.</param>
    /// <param name="fileLockManager">The shared file lock manager for concurrent edits.</param>
    /// <param name="orchestrator">The orchestrator (for spawning sub-agents).</param>
    /// <param name="maxIterations">Maximum number of tool-call loop iterations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the agent run.</returns>
    Task<AgentRunResult> RunAsync(
        string task,
        JsonElement toolsDef,
        AgentToolRegistry toolRegistry,
        FileLockManager fileLockManager,
        AgentOrchestrator orchestrator,
        int maxIterations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the agent's tool-calling loop with a pre-built conversation history.
    /// Unlike <see cref="RunAsync"/>, this does not add a user message or build
    /// an initial request history — the caller provides the full history.
    /// Messages (assistant, tool) are appended to both the internal
    /// <see cref="Messages"/> list and the provided <paramref name="requestHistory"/>.
    ///
    /// This is used by the main AI chat panel so that context-window trimming,
    /// system-prompt merging, and image injection happen once in the caller, then
    /// the agent handles only the tool-calling loop.
    /// </summary>
    /// <param name="requestHistory">
    /// The full conversation history to send. Must end with a user message.
    /// Mutated in-place — assistant and tool messages are appended during the loop.
    /// </param>
    /// <param name="toolsDef">Pre-serialized tool definitions to send.</param>
    /// <param name="toolRegistry">The tool registry for executing tool calls.</param>
    /// <param name="fileLockManager">The shared file lock manager for concurrent edits.</param>
    /// <param name="orchestrator">The orchestrator (for spawning sub-agents).</param>
    /// <param name="maxIterations">Maximum number of tool-call loop iterations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the agent run.</returns>
    Task<AgentRunResult> RunWithHistoryAsync(
        List<AiChatMessage> requestHistory,
        JsonElement toolsDef,
        AgentToolRegistry toolRegistry,
        FileLockManager fileLockManager,
        AgentOrchestrator orchestrator,
        int maxIterations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message to this agent's conversation history.
    /// </summary>
    void AddMessage(AiChatMessage message);

    /// <summary>
    /// Called by the orchestrator when a child agent reports its result.
    /// The result is added as a tool message from the "spawn_agent" tool.
    /// </summary>
    void ReceiveChildResult(string childAgentId, AgentRunResult result);

    /// <summary>
    /// Adds a child agent ID to this agent's children set.
    /// </summary>
    void RegisterChild(string childAgentId);

    /// <summary>
    /// Removes a child agent ID from this agent's children set.
    /// </summary>
    void UnregisterChild(string childAgentId);
}
