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
            return [];
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
                // v4 adds the default-preset flag (IsDefault). v3 added per-tool
                // hidden (disabled) parameters. v2 added per-tool description
                // overrides, pinned parameters, and backend option overrides.
                // Older files load fine (new members default to null/false).
                SchemaVersion = 4,
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
