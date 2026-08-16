namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// The kinds of live progress an agent reports while it runs.
///
/// These are broadcast through <see cref="IAgent.Activity"/> so any number of
/// observers (the chat panel's agent session view, the orchestrator tree, …) can
/// follow an agent regardless of who dispatched it. This is deliberately separate
/// from <see cref="Agent.IterationCallback"/>, which is a single-slot callback
/// owned by whichever code started the run.
/// </summary>
internal enum AgentActivityKind
{
    /// <summary>A new tool-call loop iteration is about to start.</summary>
    IterationStarted,

    /// <summary>One or more messages were appended to the agent's history.</summary>
    MessagesChanged,

    /// <summary>The run finished — successfully, cancelled, or with an error.</summary>
    RunCompleted
}

/// <summary>Payload for <see cref="IAgent.Activity"/>.</summary>
internal sealed class AgentActivityEventArgs : EventArgs
{
    /// <summary>What the agent just did.</summary>
    public AgentActivityKind Kind { get; }

    /// <summary>
    /// The zero-based tool-call loop iteration the agent is on, or -1 when the
    /// activity did not happen inside the loop.
    /// </summary>
    public int Iteration { get; }

    public AgentActivityEventArgs(AgentActivityKind kind, int iteration = -1)
    {
        Kind = kind;
        Iteration = iteration;
    }
}

/// <summary>
/// Payload for <see cref="IAgent.TokenStreamed"/>, carrying a single streaming
/// token as it arrives from the provider.
/// </summary>
internal sealed class AgentTokenEventArgs : EventArgs
{
    /// <summary>The streamed token (content, reasoning, or tool call).</summary>
    public AiStreamToken Token { get; }

    public AgentTokenEventArgs(AiStreamToken token)
    {
        Token = token;
    }
}
