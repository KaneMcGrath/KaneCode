using System.Text.Json;

namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// Tool that allows an agent to spawn a sub-agent to handle a delegated task.
///
/// The sub-agent runs independently with its own context window and tool access,
/// then reports its result back to the parent agent. Sub-agents can use different
/// providers, models, or modes than their parent.
///
/// This tool is intercepted by <see cref="AgentOrchestrator.OnToolExecution"/>
/// which creates the sub-agent, runs it, and returns the result.
/// </summary>
internal sealed class SpawnAgentTool : IAgentTool
{
    public string Name => "spawn_agent";

    public string Description =>
        "Spawns a sub-agent to handle a delegated task independently. " +
        "The sub-agent has its own context window and can use tools. " +
        "Use this for parallel work, specialized analysis, or tasks that " +
        "benefit from a focused context. The sub-agent returns a summary when done. " +
        "Do NOT use spawn_agent for very small, single-step operations.";

    public string Category => "Agent";

    public JsonElement ParametersSchema { get; }

    public SpawnAgentTool()
    {
        using System.IO.MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");

            writer.WriteStartObject("properties");

            writer.WriteStartObject("task");
            writer.WriteString("type", "string");
            writer.WriteString("description", "The task description for the sub-agent to execute. Be specific about what you want the sub-agent to accomplish.");
            writer.WriteEndObject();

            writer.WriteStartObject("displayName");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Optional display name for the sub-agent (shown in logs). If omitted, a name will be auto-generated.");
            writer.WriteEndObject();

            writer.WriteStartObject("provider");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Optional provider ID for the sub-agent (e.g. 'v1chatcompletions'). If omitted, the parent's provider is used.");
            writer.WriteEndObject();

            writer.WriteStartObject("model");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Optional model for the sub-agent. If omitted, the parent's model is used.");
            writer.WriteEndObject();

            writer.WriteStartObject("mode");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Optional chat mode ID for the sub-agent (e.g. 'agent', 'chat'). If omitted, the parent's mode is used.");
            writer.WriteEndObject();

            writer.WriteStartObject("systemPrompt");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Optional custom system prompt for the sub-agent. Overrides the mode's default prompt.");
            writer.WriteEndObject();

            writer.WriteStartObject("maxIterations");
            writer.WriteString("type", "integer");
            writer.WriteString("description", "Maximum number of tool-call loop iterations for the sub-agent (default: 50, max: 200).");
            writer.WriteEndObject();

            writer.WriteEndObject(); // properties

            writer.WriteStartArray("required");
            writer.WriteStringValue("task");
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        ParametersSchema = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        // This tool is always intercepted by AgentOrchestrator.OnToolExecution.
        // If it reaches here directly (outside the orchestrator), return an error.
        return Task.FromResult(ToolCallResult.Fail(
            "The spawn_agent tool must be executed within an AgentOrchestrator context. " +
            "Ensure the multi-agent framework is initialized."));
    }
}
