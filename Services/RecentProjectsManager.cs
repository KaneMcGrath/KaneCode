using KaneCode.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace KaneCode.Services;

/// <summary>
/// Manages persistence of the recent projects/solutions/folders list.
/// Entries are stored per-user under <c>%LocalAppData%\KaneCode\recent-projects.json</c>.
/// </summary>
internal sealed class RecentProjectsManager
{
    private const int DefaultMaxEntries = 15;

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KaneCode");

    private static readonly string StorageFilePath = Path.Combine(SettingsDirectory, "recent-projects.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly int _maxEntries;
    private readonly List<RecentProjectItem> _entries;

    /// <summary>
    /// Initializes a new instance and loads persisted entries from disk.
    /// </summary>
    /// <param name="maxEntries">Maximum number of recent items to keep. Defaults to 15.</param>
    public RecentProjectsManager(int maxEntries = DefaultMaxEntries)
    {
        _maxEntries = maxEntries;
        _entries = LoadFromDisk();
    }

    /// <summary>
    /// Returns a snapshot of all recent entries, ordered by most recently opened first.
    /// </summary>
    public IReadOnlyList<RecentProjectItem> GetEntries()
    {
        lock (_entries)
        {
            return _entries.OrderByDescending(e => e.LastOpened).ToList();
        }
    }

    /// <summary>
    /// Records that a project, solution, or folder was opened.
    /// Adds or updates the entry, trims to <see cref="_maxEntries"/>, and persists.
    /// </summary>
    public void TrackOpen(string fullPath, RecentItemType itemType)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        lock (_entries)
        {
            var existing = _entries.FirstOrDefault(e =>
                string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                // Move to top and update timestamp
                existing.LastOpened = DateTime.UtcNow;
            }
            else
            {
                _entries.Add(new RecentProjectItem(fullPath, itemType, DateTime.UtcNow));
            }

            // Trim to max entries (keep most recent)
            var trimmed = _entries
                .OrderByDescending(e => e.LastOpened)
                .Take(_maxEntries)
                .ToList();

            _entries.Clear();
            _entries.AddRange(trimmed);
        }

        SaveToDisk();
    }

    /// <summary>
    /// Removes a specific entry from the recent list and persists.
    /// </summary>
    public void RemoveEntry(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        lock (_entries)
        {
            var removed = _entries.RemoveAll(e =>
                string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                SaveToDisk();
            }
        }
    }

    /// <summary>
    /// Clears all recent entries and persists the empty list.
    /// </summary>
    public void ClearAll()
    {
        lock (_entries)
        {
            _entries.Clear();
        }

        SaveToDisk();
    }

    /// <summary>
    /// Determines the <see cref="RecentItemType"/> based on file extension or directory status.
    /// </summary>
    public static RecentItemType DetermineItemType(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Directory.Exists(path))
        {
            return RecentItemType.Folder;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return RecentItemType.Folder;
        }

        return ext.ToLowerInvariant() switch
        {
            ".sln" or ".slnx" => RecentItemType.Solution,
            ".csproj" or ".vbproj" or ".fsproj" => RecentItemType.Project,
            _ => RecentItemType.Folder
        };
    }

    /// <summary>
    /// Populates an <see cref="ObservableCollection{T}"/> from the current entries
    /// (most recent first). The collection is cleared and repopulated.
    /// </summary>
    public void PopulateCollection(ObservableCollection<RecentProjectItem> target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var entries = GetEntries();

        target.Clear();

        foreach (var entry in entries)
        {
            target.Add(entry);
        }
    }

    // ── Persistence ──────────────────────────────────────────────────

    private List<RecentProjectItem> LoadFromDisk()
    {
        if (!File.Exists(StorageFilePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(StorageFilePath);
            var dto = JsonSerializer.Deserialize<RecentProjectsDto>(json);
            return dto?.Entries is not null
                ? dto.Entries
                    .Select(e => new RecentProjectItem(e.FullPath, e.ItemType, e.LastOpened))
                    .OrderByDescending(e => e.LastOpened)
                    .Take(_maxEntries)
                    .ToList()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            List<RecentProjectItem> snapshot;
            lock (_entries)
            {
                snapshot = _entries
                    .OrderByDescending(e => e.LastOpened)
                    .Take(_maxEntries)
                    .ToList();
            }

            var dto = new RecentProjectsDto
            {
                Entries = snapshot
                    .Select(e => new RecentProjectEntryDto
                    {
                        FullPath = e.FullPath,
                        ItemType = e.ItemType,
                        LastOpened = e.LastOpened
                    })
                    .ToList()
            };

            string json = JsonSerializer.Serialize(dto, JsonOptions);
            File.WriteAllText(StorageFilePath, json);
        }
        catch (IOException)
        {
            // Best effort — don't crash if settings can't be saved
        }
    }

    // ── DTO for serialization ────────────────────────────────────────

    private sealed class RecentProjectsDto
    {
        public List<RecentProjectEntryDto> Entries { get; set; } = [];
    }

    private sealed class RecentProjectEntryDto
    {
        public string FullPath { get; set; } = string.Empty;
        public RecentItemType ItemType { get; set; }
        public DateTime LastOpened { get; set; }
    }
}
