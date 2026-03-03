using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Manages persistence of AI provider settings including DPAPI-encrypted API keys.
/// Settings are stored per-user under <c>%LocalAppData%\KaneCode\ai-settings.json</c>.
/// </summary>
internal static class AiSettingsManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KaneCode");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "ai-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads all saved provider settings from disk, decrypting API keys.
    /// Returns an empty list when no settings file exists or on read failure.
    /// </summary>
    public static List<AiProviderSettings> Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var entries = JsonSerializer.Deserialize<List<AiSettingsDto>>(json) ?? [];

            return entries.Select(dto => new AiProviderSettings
            {
                ProviderId = dto.ProviderId,
                Label = dto.Label,
                Endpoint = dto.Endpoint,
                ApiKey = DecryptApiKey(dto.EncryptedApiKey),
                SelectedModel = dto.SelectedModel,
                IsActive = dto.IsActive
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException)
        {
            return [];
        }
    }

    /// <summary>
    /// Persists the given provider settings to disk, encrypting API keys with DPAPI.
    /// </summary>
    public static void Save(IReadOnlyList<AiProviderSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var entries = settings.Select(s => new AiSettingsDto
            {
                ProviderId = s.ProviderId,
                Label = s.Label,
                Endpoint = s.Endpoint,
                EncryptedApiKey = EncryptApiKey(s.ApiKey),
                SelectedModel = s.SelectedModel,
                IsActive = s.IsActive
            }).ToList();

            var json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (IOException)
        {
            // Best effort — don't crash if settings can't be saved
        }
    }

    /// <summary>
    /// Encrypts a plaintext API key using DPAPI (CurrentUser scope).
    /// Returns an empty string for null/empty input.
    /// </summary>
    private static string EncryptApiKey(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    /// <summary>
    /// Decrypts a DPAPI-encrypted API key back to plaintext.
    /// Returns an empty string for null/empty input or on decryption failure.
    /// </summary>
    private static string DecryptApiKey(string? encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
        {
            return string.Empty;
        }

        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedBase64);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// DTO for JSON serialization — stores the API key in encrypted form.
    /// </summary>
    private sealed class AiSettingsDto
    {
        public string ProviderId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string EncryptedApiKey { get; set; } = string.Empty;
        public string SelectedModel { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}
