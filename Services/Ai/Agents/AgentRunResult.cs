namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// Encapsulates the result of running an agent's tool-calling loop.
/// </summary>
internal sealed record AgentRunResult
{
    /// <summary>Whether the agent completed its task successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The final textual summary from the agent.
    /// When <see cref="Success"/> is true, this is the agent's conclusion.
    /// When false, this describes what went wrong.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// The number of tool-call iterations the agent executed.
    /// </summary>
    public int Iterations { get; init; }

    /// <summary>
    /// The total number of tool calls the agent made across all iterations.
    /// </summary>
    public int ToolCallCount { get; init; }

    /// <summary>
    /// Total token usage across all provider calls made by this agent.
    /// </summary>
    public AiUsageStats? UsageStats { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static AgentRunResult Ok(string summary, int iterations, int toolCallCount, AiUsageStats? usageStats = null)
    {
        return new AgentRunResult
        {
            Success = true,
            Summary = summary,
            Iterations = iterations,
            ToolCallCount = toolCallCount,
            UsageStats = usageStats
        };
    }

    /// <summary>Creates a failed result.</summary>
    public static AgentRunResult Fail(string error, int iterations = 0, int toolCallCount = 0)
    {
        return new AgentRunResult
        {
            Success = false,
            Summary = error,
            Iterations = iterations,
            ToolCallCount = toolCallCount
        };
    }
}
