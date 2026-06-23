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
                SchemaVersion = 1,
                Presets = [.. presets]
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
