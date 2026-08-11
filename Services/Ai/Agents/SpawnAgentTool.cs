using KaneCode.Models;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// Tool that allows an agent to spawn a sub-agent to handle a delegated task.
///
/// The sub-agent runs independently with its own context window and tool access,
/// then reports its result back to the parent agent. Sub-agents can use different
/// providers, models, or presets than their parent.
///
/// This tool is intercepted by <see cref="AgentOrchestrator.OnToolExecution"/>
/// which creates the sub-agent, runs it, and returns the result.
/// </summary>
internal sealed class SpawnAgentTool : IAgentTool
{
    private const string BaseDescription =
        "Spawns a sub-agent to handle a delegated task independently. " +
        "The sub-agent has its own context window and can use tools. " +
        "Use this for parallel work, specialized analysis, or tasks that " +
        "benefit from a focused context. The sub-agent returns a summary when done. " +
        "Do NOT use spawn_agent for very small, single-step operations.";

    public string Name => "spawn_agent";

    /// <summary>
    /// The description is computed dynamically so presets marked as subagents
    /// (and their short descriptions) appear in the tool description immediately
    /// after they are saved in the Preset Editor. The description is resolved at
    /// serialization time by <see cref="AgentToolRegistry"/>.
    /// </summary>
    public string Description => BuildDescription(BaseDescription, AiPresetManager.LoadSubagentPresets());

    public string Category => "Agent";

    /// <summary>
    /// Built per access so the <c>preset</c> parameter description stays in sync
    /// with the presets currently marked as subagents.
    /// </summary>
    public JsonElement ParametersSchema => BuildParametersSchema();

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        // This tool is always intercepted by AgentOrchestrator.OnToolExecution.
        // If it reaches here directly (outside the orchestrator), return an error.
        return Task.FromResult(ToolCallResult.Fail(
            "The spawn_agent tool must be executed within an AgentOrchestrator context. " +
            "Ensure the multi-agent framework is initialized."));
    }

    /// <summary>
    /// Builds the tool description: the base guidance followed by the list of
    /// presets currently marked as subagents. When none are configured, the
    /// description points the model at the Preset Editor.
    /// </summary>
    internal static string BuildDescription(string baseDescription, IReadOnlyList<AiPreset> subagentPresets)
    {
        ArgumentNullException.ThrowIfNull(baseDescription);
        ArgumentNullException.ThrowIfNull(subagentPresets);

        StringBuilder builder = new(baseDescription);
        builder.AppendLine();
        builder.AppendLine();

        if (subagentPresets.Count == 0)
        {
            builder.Append(
                "No subagent presets are configured. Open the Preset Editor and check " +
                "\"Set as subagent\" on a preset to make it available as a sub-agent.");
            return builder.ToString();
        }

        builder.Append("Available subagent presets (pass the name via the 'preset' parameter):");
        foreach (AiPreset preset in subagentPresets)
        {
            builder.AppendLine();
            builder.Append("- ").Append(preset.Name);
            if (!string.IsNullOrWhiteSpace(preset.SubagentDescription))
            {
                builder.Append(" — ").Append(preset.SubagentDescription.Trim());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the description for the <c>preset</c> parameter, listing the names
    /// of the presets currently marked as subagents so the model knows which
    /// values are valid.
    /// </summary>
    internal static string BuildPresetParameterDescription(IReadOnlyList<AiPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        IReadOnlyList<AiPreset> subagents = presets
            .Where(p => p.IsSubagent)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subagents.Count == 0)
        {
            return
                "Optional name of a subagent preset to use for the sub-agent. " +
                "No subagent presets are currently configured — open the Preset Editor " +
                "and check \"Set as subagent\" on a preset to enable this. " +
                "If omitted, the parent's mode is used.";
        }

        return
            "Optional name of a subagent preset to use for the sub-agent " +
            $"(e.g. '{subagents[0].Name}'). The sub-agent runs with the preset's system " +
            "prompt and tool configuration. If omitted, the parent's mode is used. " +
            "Available subagent presets: " +
            string.Join(", ", subagents.Select(p => p.Name)) + ".";
    }

    private static JsonElement BuildParametersSchema()
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
            writer.WriteString("description", "Optional provider for the sub-agent — the provider ID or the user-created label from AI settings (e.g. 'v1chatcompletions' or 'My OpenAI Key'). If omitted, the parent's provider is used.");
            writer.WriteEndObject();

            writer.WriteStartObject("model");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Optional model for the sub-agent. A combined 'providerLabel/model' value (e.g. 'My OpenAI Key/gpt-4o') also selects the provider. If omitted, the parent's model is used.");
            writer.WriteEndObject();

            writer.WriteStartObject("preset");
            writer.WriteString("type", "string");
            writer.WriteString("description", BuildPresetParameterDescription(AiPresetManager.Load()));
            writer.WriteEndObject();

            writer.WriteStartObject("systemPrompt");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Optional custom system prompt for the sub-agent. Overrides the preset's (or mode's) default prompt.");
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

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
