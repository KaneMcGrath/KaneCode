using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Tickets;

/// <summary>
/// Persists global ticket-system settings under
/// <c>PortablePathProvider.BaseDirectory\ticket-settings.json</c>.
/// </summary>
internal static class TicketSettingsManager
{
    private static readonly string SettingsDirectory = PortablePathProvider.BaseDirectory;

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "ticket-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads the ticket settings, returning defaults when the file is absent or corrupt.
    /// </summary>
    public static TicketSettings Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new TicketSettings();
        }

        try
        {
            string json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<TicketSettings>(json, JsonOptions) ?? new TicketSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new TicketSettings();
        }
    }

    /// <summary>Persists the ticket settings, creating the directory if needed.</summary>
    public static void Save(TicketSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (IOException)
        {
            // Best effort — do not crash if settings cannot be saved.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>
/// Global ticket-system configuration.
/// </summary>
internal sealed class TicketSettings
{
    /// <summary>
    /// When true, tickets may override the active provider/model/agent mode via their
    /// header options. When false, any ticket that sets one of those options is marked
    /// <see cref="Models.TicketStatus.Blocked"/> so untrusted ticket files cannot
    /// silently redirect agent work to an unexpected model or provider.
    /// </summary>
    public bool AllowTicketOverrides { get; set; }

    /// <summary>
    /// Maximum number of tickets an agent may work on concurrently.
    /// </summary>
    public int MaxConcurrentTickets { get; set; } = 1;
}
