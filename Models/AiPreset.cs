namespace KaneCode.Models;

/// <summary>
/// Represents a user-defined AI chat mode preset.
/// Stores a name, an optional system prompt, and an optional set of allowed tool names.
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
}
