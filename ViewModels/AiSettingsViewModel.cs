using KaneCode.Infrastructure;
using KaneCode.Services.Ai;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;

namespace KaneCode.ViewModels;

/// <summary>
/// View model for the AI Providers settings page. Allows adding, removing,
/// and configuring AI provider entries with encrypted API key storage.
/// </summary>
internal sealed class AiSettingsViewModel : ObservableObject
{
    private AiProviderEntryViewModel? _selectedEntry;

    public AiSettingsViewModel()
    {
        var saved = AiSettingsManager.Load();
        foreach (var s in saved)
        {
            Entries.Add(new AiProviderEntryViewModel(NormalizeSettings(s)));
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
    /// </summary>
    public static IReadOnlyList<string> ProviderTypes { get; } =
    [
        "openai"
    ];

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
            return "openai";
        }

        return string.IsNullOrWhiteSpace(providerId) ? "openai" : providerId;
    }

    private void AddEntry()
    {
        var entry = new AiProviderEntryViewModel(new AiProviderSettings
        {
            ProviderId = "openai",
            Label = "New Provider"
        });

        Entries.Add(entry);
        SelectedEntry = entry;
    }

    private void RemoveSelected()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        Entries.Remove(SelectedEntry);
        SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
    }

    /// <summary>
    /// Persists all entries to disk with encrypted API keys.
    /// </summary>
    public void Save()
    {
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
    private bool _isTemperatureEnabled;
    private bool _isTopPEnabled;
    private bool _isTopKEnabled;
    private bool _isMinPEnabled;
    private bool _isPresencePenaltyEnabled;
    private bool _isRepetitionPenaltyEnabled;

    public AiProviderEntryViewModel(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _providerId = string.Equals(settings.ProviderId, "llamacpp", StringComparison.OrdinalIgnoreCase)
            ? "openai"
            : settings.ProviderId;
        _label = settings.Label;
        _endpoint = settings.Endpoint;
        _apiKey = settings.ApiKey;
        _selectedModel = settings.SelectedModel;
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
        ProviderId = string.Equals(ProviderId, "llamacpp", StringComparison.OrdinalIgnoreCase)
            ? "openai"
            : ProviderId,
        Label = Label,
        Endpoint = Endpoint,
        ApiKey = ApiKey,
        SelectedModel = SelectedModel,
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
