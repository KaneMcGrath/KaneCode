namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// The role of an agent within the multi-agent hierarchy.
/// </summary>
internal enum AgentRole
{
    /// <summary>
    /// The root-level agent — typically the main AI chat.
    /// Can spawn sub-agents but has no parent.
    /// </summary>
    Root,

    /// <summary>
    /// A sub-agent spawned by a parent agent to perform a delegated task.
    /// Reports results back to its parent when finished.
    /// </summary>
    SubAgent,

    /// <summary>
    /// A top-level agent working on a KaneCode ticket. Like <see cref="Root"/> it has
    /// no parent, but it is dispatched autonomously by the ticket system rather than
    /// by the main AI chat panel.
    /// </summary>
    Ticket
}
