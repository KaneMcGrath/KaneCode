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
    /// The messages in this agent's conversation history.
    /// </summary>
    IReadOnlyList<AiChatMessage> Messages { get; }

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
