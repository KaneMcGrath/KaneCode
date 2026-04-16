using System.IO;
using System.Text.Json;

namespace KaneCode.Services;

/// <summary>
/// Manages persistence of general IDE settings.
/// Settings are stored per-user under <c>%LocalAppData%\KaneCode\general-settings.json</c>.
/// </summary>
internal static class GeneralSettingsManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KaneCode");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "general-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string GetDefaultPath() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Loads the default project folder setting from disk.
    /// Returns the user-configured value, or the default (MyDocuments) if no setting exists.
    /// </summary>
    public static string LoadDefaultProjectFolder()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return GetDefaultPath();
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var dto = JsonSerializer.Deserialize<GeneralSettingsDto>(json);
            return !string.IsNullOrWhiteSpace(dto?.DefaultProjectFolder) ? dto.DefaultProjectFolder : GetDefaultPath();
        }
        catch (Exception)
        {
            return GetDefaultPath();
        }
    }

    /// <summary>
    /// Saves the default project folder setting to disk.
    /// </summary>
    public static void SaveDefaultProjectFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var dto = new GeneralSettingsDto
            {
                DefaultProjectFolder = folderPath
            };

            string json = JsonSerializer.Serialize(dto, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (IOException)
        {
            // Best effort — don't crash if settings can't be saved
        }
    }

    private sealed class GeneralSettingsDto
    {
        public string DefaultProjectFolder { get; set; } = string.Empty;
    }
}
