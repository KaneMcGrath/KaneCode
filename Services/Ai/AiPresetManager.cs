using KaneCode.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaneCode.Services.Ai;

/// <summary>
/// Manages persistence of user-defined AI chat presets.
/// Presets are stored under <c>PortablePathProvider.BaseDirectory\ai-presets.json</c>.
/// </summary>
internal static class AiPresetManager
{
    private static readonly string SettingsDirectory = PortablePathProvider.BaseDirectory;

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "ai-presets.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    /// <summary>
    /// Fired whenever presets are saved to disk.
    /// </summary>
    internal static event EventHandler? PresetsSaved;

    /// <summary>
    /// Loads all saved presets from disk.
    /// Returns an empty list when no file exists or the file is corrupt.
    /// </summary>
    public static List<AiPreset> Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            // Fresh install — seed the built-in default subagent preset so the
            // spawn_agent tool has something to work with out of the box. It is
            // persisted (and becomes user-editable) the first time the preset
            // editor is saved.
            return [CreateDefaultSubagentPreset()];
        }

        try
        {
            string json = File.ReadAllText(SettingsFilePath);
            var container = JsonSerializer.Deserialize<PresetContainer>(json, JsonOptions);
            return container?.Presets ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns the preset marked as the user's default agent mode, or <c>null</c>
    /// when none has been chosen. When null, the built-in Agent mode is used as
    /// the default agent mode after a project is loaded.
    /// </summary>
    public static AiPreset? LoadDefaultPreset()
    {
        foreach (AiPreset preset in Load())
        {
            if (preset.IsDefault)
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// Ensures at most one preset is marked as the default agent mode.
    /// The first default found wins; any additional defaults are cleared.
    /// Returns the same list for chaining.
    /// </summary>
    internal static List<AiPreset> NormalizeDefaults(List<AiPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        bool defaultSeen = false;
        foreach (AiPreset preset in presets)
        {
            if (!preset.IsDefault)
            {
                continue;
            }

            if (defaultSeen)
            {
                preset.IsDefault = false;
            }
            else
            {
                defaultSeen = true;
            }
        }

        return presets;
    }

    /// <summary>
    /// Returns the presets currently marked as subagent presets (<see cref="AiPreset.IsSubagent"/>),
    /// ordered by name. These are the presets an agent can reference via the spawn_agent
    /// tool's <c>preset</c> parameter, and they are listed in the tool's description.
    /// On a fresh install this includes the built-in default worker.
    /// </summary>
    public static IReadOnlyList<AiPreset> LoadSubagentPresets()
    {
        return Load()
            .Where(p => p.IsSubagent)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Builds the built-in default subagent preset ("Default Worker") that seeds a
    /// fresh install so the spawn_agent tool works out of the box. It is returned by
    /// <see cref="Load"/> when no presets file exists yet, shown in the preset editor,
    /// and persisted once the user saves.
    /// </summary>
    internal static AiPreset CreateDefaultSubagentPreset()
    {
        return new AiPreset
        {
            Id = "default-subagent-worker",
            Name = "Default Worker",
            IsSubagent = true,
            SubagentDescription = "General-purpose file worker — reads, writes, edits, and searches the codebase.",
            SystemPrompt =
                "You are a general-purpose sub-agent worker. Your job is to complete the task delegated to you " +
                "by the parent agent.\n\n" +
                "Rules:\n" +
                "- Work directly in the codebase: read files before editing them, and use search to locate " +
                "relevant code.\n" +
                "- Make focused, minimal changes and verify your work by reading back edited files (run " +
                "diagnostics when useful).\n" +
                "- Use only the file-system, search, and diagnostics tools available to you — no Git, build, " +
                "NuGet, presentation, or other tools.\n" +
                "- When finished, report a concise summary of what you changed and any issues you noticed.",
            AllowedTools = new HashSet<string>(StringComparer.Ordinal)
            {
                "read", "write", "edit", "delete", "rename_path",
                "create_directory", "delete_directory", "list", "search", "get_diagnostics"
            }
        };
    }

    /// <summary>
    /// Persists the given presets to disk.
    /// </summary>
    public static void Save(IReadOnlyList<AiPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var container = new PresetContainer
            {
                // v5 adds the subagent preset flag (IsSubagent) and its description
                // (SubagentDescription). v4 added the default-preset flag (IsDefault).
                // v3 added per-tool hidden (disabled) parameters. v2 added per-tool
                // description overrides, pinned parameters, and backend option
                // overrides. Older files load fine (new members default to null/false).
                SchemaVersion = 5,
                Presets = NormalizeDefaults([.. presets])
            };

            string json = JsonSerializer.Serialize(container, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);

            PresetsSaved?.Invoke(null, EventArgs.Empty);
        }
        catch (IOException)
        {
            // Best effort — don't crash if settings can't be saved
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort
        }
    }

    private sealed class PresetContainer
    {
        public int SchemaVersion { get; set; }

        public List<AiPreset> Presets { get; set; } = [];
    }
}
