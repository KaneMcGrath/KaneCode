using KaneCode.Models;
using System.Text.Json;

namespace KaneCode.Services.Ai.Modes;

/// <summary>
/// An <see cref="IAiChatMode"/> that wraps a user-defined <see cref="AiPreset"/>.
/// The preset's name becomes the display name, and its tools and system prompt
/// are used directly.
/// </summary>
internal sealed class PresetMode : IAiChatMode
{
    private readonly AiPreset _preset;
    private readonly AgentToolRegistry _toolRegistry;

    /// <summary>
    /// Creates a mode wrapper around the given preset.
    /// The <paramref name="preset"/> reference is kept for lifetime access;
    /// the mode does not take a snapshot of its properties.
    /// </summary>
    public PresetMode(AiPreset preset, AgentToolRegistry toolRegistry)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(toolRegistry);

        _preset = preset;
        _toolRegistry = toolRegistry;

        // Use a stable id prefix so we can identify preset modes
        Id = $"preset:{preset.Id}";
        DisplayName = preset.Name;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public bool ToolsEnabled => true;

    /// <inheritdoc />
    public IReadOnlySet<string>? AllowedTools => _preset.AllowedTools;

    /// <inheritdoc />
    public JsonElement GetToolDefinitions(AgentToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!registry.HasTools)
        {
            return default;
        }

        IReadOnlySet<string>? allowedTools = _preset.AllowedTools;
        return registry.SerializeToolDefinitions(allowedTools);
    }

    /// <inheritdoc />
    public string? BuildSystemPrompt(JsonElement toolsDef)
    {
        return _preset.SystemPrompt;
    }
}
