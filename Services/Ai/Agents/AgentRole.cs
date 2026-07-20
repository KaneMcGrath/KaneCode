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
    SubAgent
}
