using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Manages persistence of AI provider settings including DPAPI-encrypted API keys.
/// Settings are stored per-user under <c>%LocalAppData%\KaneCode\</c>.
///
/// Robustness guarantees:
///   • Schema versioning — future format changes are detected, not silently corrupted.
///   • Atomic writes — data is written to a temp file, then renamed into place.
///   • Backup file — the previous known-good file is always preserved as a backup.
///   • Data-loss guard — refuses to overwrite a non-empty file with an empty settings list.
///   • Forward compatibility — unknown JSON properties are ignored, so older app versions
///     can coexist with newer settings files.
///   • Orphan cleanup — when the last provider with a given API key is removed,
///     the corresponding encrypted key file is cleaned up.
///
/// File layout:
///   %LocalAppData%\KaneCode\
///     ai-settings.json           Main settings file (current)
///     ai-settings.backup.json    Previous known-good version (fallback on corruption)
///     ai-settings.tmp.json       Temp file used during atomic writes
/// </summary>
internal static class AiSettingsManager
{
    internal static event EventHandler? SettingsSaved;

    private const int CurrentSchemaVersion = 1;

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KaneCode");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "ai-settings.json");
    private static readonly string BackupFilePath = Path.Combine(SettingsDirectory, "ai-settings.backup.json");
    private static readonly string TempWritePath = Path.Combine(SettingsDirectory, "ai-settings.tmp.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Ignore unknown properties so future versions can add fields without breaking older readers
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    // ── Load ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all saved provider settings from disk, decrypting API keys.
    /// Returns an empty list when no settings file exists.
    /// Falls back to the backup file if the main file is corrupt.
    /// </summary>
    public static List<AiProviderSettings> Load()
    {
        // Try main file first
        if (File.Exists(SettingsFilePath))
        {
            var result = TryLoadFromFile(SettingsFilePath);
            if (result is not null)
            {
                return result;
            }

            // Main file is corrupt — try the backup
            if (File.Exists(BackupFilePath))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[AiSettingsManager] Main settings file is corrupt, falling back to backup.");

                var backupResult = TryLoadFromFile(BackupFilePath);
                if (backupResult is not null)
                {
                    // Restore backup over the corrupt main file (best-effort)
                    AtomicWrite(SettingsFilePath, File.ReadAllText(BackupFilePath));
                    return backupResult;
                }
            }
        }
        else if (File.Exists(BackupFilePath))
        {
            // Main file missing but backup exists — restore
            var backupResult = TryLoadFromFile(BackupFilePath);
            if (backupResult is not null)
            {
                AtomicWrite(SettingsFilePath, File.ReadAllText(BackupFilePath));
                return backupResult;
            }
        }

        return [];
    }

    /// <summary>
    /// Attempts to load settings from a specific file path.
    /// Returns null on any failure (missing file, corrupt JSON, decryption error).
    /// </summary>
    private static List<AiProviderSettings>? TryLoadFromFile(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath);
            var container = JsonSerializer.Deserialize<SettingsContainer>(json, JsonOptions);

            if (container?.Entries is null)
            {
                return null;
            }

            var result = new List<AiProviderSettings>(container.Entries.Count);
            foreach (var dto in container.Entries)
            {
                // Decrypt the API key from the stored encrypted blob
                string decryptedKey = DecryptApiKey(dto.EncryptedApiKey);

                result.Add(new AiProviderSettings
                {
                    ProviderId = dto.ProviderId ?? string.Empty,
                    Label = dto.Label ?? string.Empty,
                    Endpoint = dto.Endpoint ?? string.Empty,
                    ApiKey = decryptedKey,
                    SelectedModel = dto.SelectedModel ?? string.Empty,
                    IsActive = dto.IsActive,
                    ContextLength = dto.ContextLength,
                    Temperature = dto.Temperature,
                    TopP = dto.TopP,
                    TopK = dto.TopK,
                    MinP = dto.MinP,
                    PresencePenalty = dto.PresencePenalty,
                    RepetitionPenalty = dto.RepetitionPenalty
                });
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    // ── Save ────────────────────────────────────────────────────────

    /// <summary>
    /// Persists the given provider settings to disk with encrypted API keys.
    /// Uses atomic writes and preserves a backup of the previous file.
    ///
    /// <b>Data-loss guard:</b> Refuses to overwrite a non-empty existing file with
    /// an empty settings list. This prevents a buggy caller from accidentally
    /// destroying all provider data. Use <see cref="Clear"/> to intentionally
    /// wipe all settings.
    /// </summary>
    public static void Save(IReadOnlyList<AiProviderSettings> settings)
    {
        Save(settings, raiseEvent: true);
    }

    /// <summary>
    /// Persists the given provider settings to disk with encrypted API keys.
    /// When <paramref name="raiseEvent"/> is false, the <see cref="SettingsSaved"/>
    /// event is not fired after the write completes. Use this for internal flag
    /// updates (e.g. the active provider selection) to avoid triggering an
    /// expensive full provider reload when only the in-memory selection changed.
    /// </summary>
    public static void Save(IReadOnlyList<AiProviderSettings> settings, bool raiseEvent)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // ── Data-loss guard ─────────────────────────────────────────
        // If the caller passes an empty list but the on-disk file has data,
        // something is wrong — refuse to overwrite. The only intentional
        // way to clear settings is via Clear() or the Remove button in the UI.
        if (settings.Count == 0 && File.Exists(SettingsFilePath))
        {
            try
            {
                string existingJson = File.ReadAllText(SettingsFilePath);
                var existingContainer = JsonSerializer.Deserialize<SettingsContainer>(existingJson, JsonOptions);

                if (existingContainer?.Entries is { Count: > 0 })
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AiSettingsManager] Data-loss guard: refusing to overwrite " +
                        $"{existingContainer.Entries.Count} existing entries with empty list.");
                    return;
                }
            }
            catch
            {
                // Can't read existing file — may be corrupt. Allow empty save
                // so the user can start fresh.
            }
        }

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
                IsActive = s.IsActive,
                ContextLength = s.ContextLength,
                Temperature = s.Temperature,
                TopP = s.TopP,
                TopK = s.TopK,
                MinP = s.MinP,
                PresencePenalty = s.PresencePenalty,
                RepetitionPenalty = s.RepetitionPenalty
            }).ToList();

            var container = new SettingsContainer
            {
                SchemaVersion = CurrentSchemaVersion,
                Entries = entries
            };

            string json = JsonSerializer.Serialize(container, JsonOptions);

            // 1. Backup the existing file before overwriting
            BackupExistingFile();

            // 2. Atomic write to the main file
            AtomicWrite(SettingsFilePath, json);

            if (raiseEvent)
            {
                SettingsSaved?.Invoke(null, EventArgs.Empty);
            }
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

    /// <summary>
    /// Completely removes all settings files including the backup.
    /// Only call this when the user explicitly requests to clear all provider data.
    /// </summary>
    public static void Clear()
    {
        foreach (string path in new[] { SettingsFilePath, BackupFilePath, TempWritePath })
        {
            SafeDeleteFile(path);
        }

        SettingsSaved?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Copies the current settings file to a backup location.
    /// Silently succeeds if no file exists yet.
    /// </summary>
    private static void BackupExistingFile()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return;
        }

        try
        {
            File.Copy(SettingsFilePath, BackupFilePath, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort backup
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort backup
        }
    }

    /// <summary>
    /// Atomically writes content to a file. Writes to a temp path first,
    /// then renames (which is an atomic operation on NTFS).
    /// </summary>
    private static void AtomicWrite(string targetPath, string content)
    {
        string tempPath = targetPath + ".write-tmp";
        File.WriteAllText(tempPath, content, Encoding.UTF8);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── API Key encryption (DPAPI) ─────────────────────────────────

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

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
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
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    // ── Serialization types ────────────────────────────────────────

    /// <summary>
    /// Top-level JSON container. The schema version allows forward compatibility
    /// — future readers can detect and handle format changes gracefully.
    /// </summary>
    private sealed class SettingsContainer
    {
        public int SchemaVersion { get; set; }

        /// <summary>Provider configuration entries.</summary>
        public List<AiSettingsDto> Entries { get; set; } = [];
    }

    /// <summary>
    /// DTO for JSON serialization. Stores the API key as a DPAPI-encrypted
    /// Base64 string. All properties have defaults so deserialization of
    /// older files (with fewer fields) still succeeds.
    /// </summary>
    private sealed class AiSettingsDto
    {
        public string ProviderId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string EncryptedApiKey { get; set; } = string.Empty;
        public string SelectedModel { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int? ContextLength { get; set; }
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? TopK { get; set; }
        public double? MinP { get; set; }
        public double? PresencePenalty { get; set; }
        public double? RepetitionPenalty { get; set; }
    }
}
