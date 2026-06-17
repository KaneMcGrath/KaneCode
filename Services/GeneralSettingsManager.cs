using System.IO;
using System.Text.Json;

namespace KaneCode.Services;

/// <summary>
/// Manages persistence of general IDE settings.
/// Settings are stored under <c>PortablePathProvider.BaseDirectory\general-settings.json</c>.
/// </summary>
internal static class GeneralSettingsManager
{
    private static readonly string SettingsDirectory = PortablePathProvider.BaseDirectory;

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "general-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string GetDefaultPath() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Loads the default project folder setting from disk.
    /// Returns the user-configured value, or the default (MyDocuments) if no setting exists.
    /// </summary>
    public static string LoadDefaultProjectFolder()
    {
        var dto = LoadDto();
        return !string.IsNullOrWhiteSpace(dto?.DefaultProjectFolder) ? dto.DefaultProjectFolder : GetDefaultPath();
    }

    /// <summary>
    /// Saves the default project folder setting to disk.
    /// Any existing settings (e.g. theme) are preserved.
    /// </summary>
    public static void SaveDefaultProjectFolder(string folderPath)
    {
        var dto = LoadDto() ?? new GeneralSettingsDto();
        dto.DefaultProjectFolder = folderPath;
        SaveDto(dto);
    }

    /// <summary>
    /// Loads the saved theme name from disk.
    /// Returns the saved theme name, or null if no theme has been persisted.
    /// </summary>
    public static string? LoadThemeName()
    {
        var dto = LoadDto();
        return !string.IsNullOrWhiteSpace(dto?.ThemeName) ? dto.ThemeName : null;
    }

    /// <summary>
    /// Saves the theme name to disk.
    /// Any existing settings (e.g. default project folder) are preserved.
    /// </summary>
    public static void SaveThemeName(string themeName)
    {
        var dto = LoadDto() ?? new GeneralSettingsDto();
        dto.ThemeName = themeName;
        SaveDto(dto);
    }

    /// <summary>
    /// Loads the full DTO from disk, or returns null if the file does not exist or is corrupt.
    /// </summary>
    private static GeneralSettingsDto? LoadDto()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<GeneralSettingsDto>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Persists the DTO to disk, creating the directory if necessary.
    /// </summary>
    private static void SaveDto(GeneralSettingsDto dto)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
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

        public string? ThemeName { get; set; }
    }
}
