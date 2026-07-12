using KaneCode.Infrastructure;
using KaneCode.Services.Ai;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

namespace KaneCode.ViewModels;

/// <summary>
/// View model for the AI Providers settings page. Allows adding, removing,
/// and configuring AI provider entries with encrypted API key storage.
/// All changes (add, remove, edit) are persisted immediately so the
/// <see cref="AiProviderRegistry"/> can pick them up without restarting the app.
/// Property-change-triggered saves are debounced (300ms) to avoid excessive
/// I/O and provider-reloads during rapid typing.
/// </summary>
internal sealed class AiSettingsViewModel : ObservableObject, IDisposable
{
    private AiProviderEntryViewModel? _selectedEntry;
    private int _deferSaveCount;
    private Timer? _debounceTimer;
    private bool _disposed;

    public AiSettingsViewModel()
    {
        var saved = AiSettingsManager.Load();
        foreach (var s in saved)
        {
            var entry = new AiProviderEntryViewModel(NormalizeSettings(s));
            AddAndTrack(entry);
            Entries.Add(entry);
        }

        AddCommand = new RelayCommand(_ => AddEntry());
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedEntry is not null);
        SaveCommand = new RelayCommand(_ => Save());
    }

    /// <summary>
    /// All configured provider entries.
    /// </summary>
    public ObservableCollection<AiProviderEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Currently selected entry in the list.
    /// </summary>
    public AiProviderEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SaveCommand { get; }

    /// <summary>
    /// Known provider types for the combo box.
    /// These are the display-friendly names shown in the UI.
    /// Use <see cref="DisplayToProviderId"/> to map to internal identifiers.
    /// </summary>
    public static IReadOnlyList<string> ProviderTypes { get; } =
    [
        "/v1/completions"
    ];

    /// <summary>
    /// Maps the display-friendly combo-box value (e.g. "/v1/completions")
    /// back to the internally persisted provider ID (e.g. "v1completions").
    /// </summary>
    internal static string DisplayToProviderId(string? displayName)
    {
        return displayName switch
        {
            "/v1/completions" => "v1completions",
            null or "" => "v1completions",
            _ => displayName
        };
    }

    /// <summary>
    /// Maps an internally persisted provider ID (e.g. "v1completions")
    /// to its display-friendly combo-box value (e.g. "/v1/completions").
    /// </summary>
    internal static string ProviderIdToDisplay(string? providerId)
    {
        return providerId switch
        {
            "v1completions" => "/v1/completions",
            null or "" => "/v1/completions",
            _ => providerId
        };
    }

    private static AiProviderSettings NormalizeSettings(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.ProviderId = NormalizeProviderId(settings.ProviderId);
        settings.ContextLength ??= AiProviderSettings.DefaultContextLength;
        return settings;
    }

    private static string NormalizeProviderId(string? providerId)
    {
        if (string.Equals(providerId, "llamacpp", StringComparison.OrdinalIgnoreCase))
        {
            return "v1completions";
        }

        return string.IsNullOrWhiteSpace(providerId) ? "v1completions" : providerId;
    }

    /// <summary>
    /// Temporarily defers auto-save. Call <see cref="ResumeSave"/> to re-enable.
    /// Auto-save is deferred during bulk operations so the registry only reloads once.
    /// </summary>
    public void DeferSave()
    {
        _deferSaveCount++;
    }

    /// <summary>
    /// Re-enables auto-save and performs an immediate save if it was previously deferred.
    /// </summary>
    public void ResumeSave()
    {
        if (_deferSaveCount > 0)
        {
            _deferSaveCount--;
        }

        if (_deferSaveCount == 0)
        {
            SaveImmediate();
        }
    }

    /// <summary>
    /// Disposes the debounce timer to avoid resource leaks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    private void AddEntry()
    {
        var entry = new AiProviderEntryViewModel(new AiProviderSettings
        {
            ProviderId = "v1completions",
            Label = "New Provider"
        });

        AddAndTrack(entry);
        Entries.Add(entry);
        SelectedEntry = entry;
        SaveImmediate();
    }

    private void RemoveSelected()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        // If the removed entry was the active provider, clear the active flag
        // and mark the next available entry as active
        bool wasActive = SelectedEntry.IsActive;
        UnsubscribeFromEntry(SelectedEntry);
        Entries.Remove(SelectedEntry);
        SelectedEntry = Entries.Count > 0 ? Entries[0] : null;

        if (wasActive && SelectedEntry is not null)
        {
            SelectedEntry.IsActive = true;
        }

        SaveImmediate();
    }

    /// <summary>
    /// Adds an entry to the internal tracking list and subscribes to its property changes
    /// so that any field edit triggers an auto-save (debounced).
    /// </summary>
    private void AddAndTrack(AiProviderEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.PropertyChanged += OnEntryPropertyChanged;
    }

    /// <summary>
    /// Unsubscribes from an entry's property changes when it is removed.
    /// </summary>
    private void UnsubscribeFromEntry(AiProviderEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.PropertyChanged -= OnEntryPropertyChanged;
    }

    /// <summary>
    /// Called when any property on any entry changes. Schedules a debounced
    /// auto-save so rapid typing doesn't trigger excessive I/O and provider reloads.
    /// </summary>
    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScheduleDebouncedSave();
    }

    /// <summary>
    /// Schedules a save to happen after a 300ms quiet period.
    /// If another property change arrives before the timer fires,
    /// the timer is reset (debouncing coalesces rapid edits).
    /// </summary>
    private void ScheduleDebouncedSave()
    {
        if (_deferSaveCount > 0)
        {
            return;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(
            _ =>
            {
                if (_disposed)
                {
                    return;
                }

                // Dispatch back to the UI thread to safely access Entries
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (!_disposed)
                    {
                        SaveImmediate();
                    }
                });
            },
            null,
            TimeSpan.FromMilliseconds(300),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Persists all entries to disk with encrypted API keys immediately.
    /// This triggers <see cref="AiProviderRegistry.Reload"/> via the
    /// <see cref="AiSettingsManager.SettingsSaved"/> event, so the changes
    /// propagate immediately to the AI Chat panel and other consumers.
    /// </summary>
    private void SaveImmediate()
    {
        if (_deferSaveCount > 0)
        {
            return;
        }

        CancelDebounce();
        Persist();
    }

    /// <summary>
    /// Manual save command — persists immediately (bypasses the debounce).
    /// </summary>
    public void Save()
    {
        SaveImmediate();
    }

    /// <summary>
    /// Cancels any pending debounced save.
    /// </summary>
    private void CancelDebounce()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    /// <summary>
    /// Performs the actual serialization and disk write via <see cref="AiSettingsManager"/>.
    /// </summary>
    private void Persist()
    {
        if (_deferSaveCount > 0)
        {
            return;
        }

        var settings = Entries.Select(e => e.ToSettings()).ToList();
        AiSettingsManager.Save(settings);
    }
}

/// <summary>
/// View model for a single AI provider configuration entry.
/// </summary>
internal sealed class AiProviderEntryViewModel : ObservableObject
{
    private string _providerId;
    private string _label;
    private string _endpoint;
    private string _apiKey;
    private string _selectedModel;
    private string _contextLength;
    private string _temperature;
    private string _topP;
    private string _topK;
    private string _minP;
    private string _presencePenalty;
    private string _repetitionPenalty;
    private bool _isActive;
    private bool _isTemperatureEnabled;
    private bool _isTopPEnabled;
    private bool _isTopKEnabled;
    private bool _isMinPEnabled;
    private bool _isPresencePenaltyEnabled;
    private bool _isRepetitionPenaltyEnabled;

    public AiProviderEntryViewModel(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Map internal provider IDs to display-friendly names for the combo box.
        // "llamacpp" is a legacy provider ID that we map to the v1/completions provider.
        string internalId = string.Equals(settings.ProviderId, "llamacpp", StringComparison.OrdinalIgnoreCase)
            ? "v1completions"
            : settings.ProviderId;
        _providerId = AiSettingsViewModel.ProviderIdToDisplay(internalId);
        _label = settings.Label;
        _endpoint = settings.Endpoint;
        _apiKey = settings.ApiKey;
        _selectedModel = settings.SelectedModel;
        IsActive = settings.IsActive;
        _contextLength = FormatNullableInt(settings.ContextLength, AiProviderSettings.DefaultContextLength);
        _temperature = FormatNullableDouble(settings.Temperature, AiProviderSettings.DefaultTemperature);
        _topP = FormatNullableDouble(settings.TopP, AiProviderSettings.DefaultTopP);
        _topK = FormatNullableInt(settings.TopK, AiProviderSettings.DefaultTopK);
        _minP = FormatNullableDouble(settings.MinP, AiProviderSettings.DefaultMinP);
        _presencePenalty = FormatNullableDouble(settings.PresencePenalty, AiProviderSettings.DefaultPresencePenalty);
        _repetitionPenalty = FormatNullableDouble(settings.RepetitionPenalty, AiProviderSettings.DefaultRepetitionPenalty);
        _isTemperatureEnabled = settings.Temperature.HasValue;
        _isTopPEnabled = settings.TopP.HasValue;
        _isTopKEnabled = settings.TopK.HasValue;
        _isMinPEnabled = settings.MinP.HasValue;
        _isPresencePenaltyEnabled = settings.PresencePenalty.HasValue;
        _isRepetitionPenaltyEnabled = settings.RepetitionPenalty.HasValue;
    }

    public string ProviderId
    {
        get => _providerId;
        set => SetProperty(ref _providerId, value);
    }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    /// <summary>
    /// Whether this provider configuration is the currently active/default provider.
    /// Preserved across save/load cycles in the settings window.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string ContextLength
    {
        get => _contextLength;
        set => SetProperty(ref _contextLength, value);
    }

    public string Temperature
    {
        get => _temperature;
        set => SetProperty(ref _temperature, value);
    }

    public bool IsTemperatureEnabled
    {
        get => _isTemperatureEnabled;
        set => SetProperty(ref _isTemperatureEnabled, value);
    }

    public string TopP
    {
        get => _topP;
        set => SetProperty(ref _topP, value);
    }

    public bool IsTopPEnabled
    {
        get => _isTopPEnabled;
        set => SetProperty(ref _isTopPEnabled, value);
    }

    public string TopK
    {
        get => _topK;
        set => SetProperty(ref _topK, value);
    }

    public bool IsTopKEnabled
    {
        get => _isTopKEnabled;
        set => SetProperty(ref _isTopKEnabled, value);
    }

    public string MinP
    {
        get => _minP;
        set => SetProperty(ref _minP, value);
    }

    public bool IsMinPEnabled
    {
        get => _isMinPEnabled;
        set => SetProperty(ref _isMinPEnabled, value);
    }

    public string PresencePenalty
    {
        get => _presencePenalty;
        set => SetProperty(ref _presencePenalty, value);
    }

    public bool IsPresencePenaltyEnabled
    {
        get => _isPresencePenaltyEnabled;
        set => SetProperty(ref _isPresencePenaltyEnabled, value);
    }

    public string RepetitionPenalty
    {
        get => _repetitionPenalty;
        set => SetProperty(ref _repetitionPenalty, value);
    }

    public bool IsRepetitionPenaltyEnabled
    {
        get => _isRepetitionPenaltyEnabled;
        set => SetProperty(ref _isRepetitionPenaltyEnabled, value);
    }

    /// <summary>
    /// Converts this view model back to a settings model for persistence.
    /// </summary>
    public AiProviderSettings ToSettings() => new()
    {
        ProviderId = AiSettingsViewModel.DisplayToProviderId(ProviderId),
        Label = Label,
        Endpoint = Endpoint,
        ApiKey = ApiKey,
        SelectedModel = SelectedModel,
        IsActive = IsActive,
        ContextLength = ParseNullableInt(ContextLength),
        Temperature = IsTemperatureEnabled ? ParseNullableDouble(Temperature) : null,
        TopP = IsTopPEnabled ? ParseNullableDouble(TopP) : null,
        TopK = IsTopKEnabled ? ParseNullableInt(TopK) : null,
        MinP = IsMinPEnabled ? ParseNullableDouble(MinP) : null,
        PresencePenalty = IsPresencePenaltyEnabled ? ParseNullableDouble(PresencePenalty) : null,
        RepetitionPenalty = IsRepetitionPenaltyEnabled ? ParseNullableDouble(RepetitionPenalty) : null
    };

    private static string FormatNullableDouble(double? value, double defaultValue)
    {
        double formattedValue = value ?? defaultValue;
        return formattedValue.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatNullableInt(int? value, int defaultValue)
    {
        int formattedValue = value ?? defaultValue;
        return formattedValue.ToString(CultureInfo.InvariantCulture);
    }

    private static double? ParseNullableDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedValue)
            ? parsedValue
            : null;
    }

    private static int? ParseNullableInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
            ? parsedValue
            : null;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? ProviderId : Label;
}
