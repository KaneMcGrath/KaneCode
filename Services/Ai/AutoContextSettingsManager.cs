using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Manages persistence of auto-context file rules. When a new AI conversation
/// is created, files matching these rules are automatically added as context.
/// </summary>
internal static class AutoContextSettingsManager
{
    private static readonly string SettingsDirectory = PortablePathProvider.BaseDirectory;

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "auto-context.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the saved auto-context rules. Returns an empty list on failure.
    /// </summary>
    public static List<string> Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(SettingsFilePath);
            AutoContextSettingsDto? dto = JsonSerializer.Deserialize<AutoContextSettingsDto>(json);
            return dto?.Rules ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Persists the given auto-context rules to disk.
    /// </summary>
    public static void Save(IReadOnlyList<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var dto = new AutoContextSettingsDto
            {
                Rules = [.. rules.Where(r => !string.IsNullOrWhiteSpace(r))]
            };

            string json = JsonSerializer.Serialize(dto, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (IOException)
        {
            // Best effort
        }
    }

    private sealed class AutoContextSettingsDto
    {
        public List<string> Rules { get; set; } = [];
    }
}
