using System.Text.Json;

namespace KaneCode.Models;

/// <summary>
/// Represents a user-defined AI chat mode preset.
/// Stores a name, an optional system prompt, an optional set of allowed tool names,
/// and per-tool overrides (description, pinned parameters, backend options).
/// Persisted by <see cref="Services.Ai.AiPresetManager"/>.
/// </summary>
internal sealed class AiPreset
{
    /// <summary>
    /// Unique identifier for this preset.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// User-visible name shown in the mode dropdown.
    /// </summary>
    public string Name { get; set; } = "New Preset";

    /// <summary>
    /// Optional custom system prompt. When null/empty, no mode-level system prompt is injected.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// The set of tool names that are allowed in this preset.
    /// <c>null</c> means all tools in the registry are available (unrestricted).
    /// An empty set means no tools are available.
    /// </summary>
    public HashSet<string>? AllowedTools { get; set; }

    /// <summary>
    /// Per-tool description override. Maps tool name to the description text that
    /// is sent to the model (with <c>{param}</c> tokens resolved at serialize time).
    /// Absent/null means the tool's canonical description is used.
    /// </summary>
    public Dictionary<string, string>? ToolDescriptions { get; set; }

    /// <summary>
    /// Per-tool pinned parameter values. Maps tool name to a map of parameter name
    /// to the locked value. Pinned values are merged over the schema defaults and
    /// sent to the model verbatim.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? PinnedParameters { get; set; }

    /// <summary>
    /// Per-tool backend option overrides. Maps tool name to a map of backend option
    /// name to the overridden value. Backend options control how a tool executes and
    /// are never serialized into the model-facing tool definition.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? ToolOptions { get; set; }

    /// <summary>
    /// Creates a deep copy of this preset. Dictionary members are copied so the
    /// clone can be edited independently (used for Revert snapshots in the editor).
    /// </summary>
    public AiPreset Clone()
    {
        return new AiPreset
        {
            Id = Id,
            Name = Name,
            SystemPrompt = SystemPrompt,
            AllowedTools = AllowedTools is null
                ? null
                : new HashSet<string>(AllowedTools, StringComparer.Ordinal),
            ToolDescriptions = ToolDescriptions is null
                ? null
                : new Dictionary<string, string>(ToolDescriptions, StringComparer.Ordinal),
            PinnedParameters = CloneNested(PinnedParameters),
            ToolOptions = CloneNested(ToolOptions)
        };
    }

    private static Dictionary<string, Dictionary<string, JsonElement>>? CloneNested(
        Dictionary<string, Dictionary<string, JsonElement>>? source)
    {
        if (source is null)
        {
            return null;
        }

        Dictionary<string, Dictionary<string, JsonElement>> clone = new(StringComparer.Ordinal);
        foreach ((string toolName, Dictionary<string, JsonElement> inner) in source)
        {
            Dictionary<string, JsonElement> innerClone = new(StringComparer.Ordinal);
            foreach ((string key, JsonElement value) in inner)
            {
                innerClone[key] = value.Clone();
            }

            clone[toolName] = innerClone;
        }

        return clone;
    }
}
