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
        settings.Temperature ??= AiProviderSettings.DefaultTemperature;
        settings.TopP ??= AiProviderSettings.DefaultTopP;
        settings.TopK ??= AiProviderSettings.DefaultTopK;
        settings.MinP ??= AiProviderSettings.DefaultMinP;
        settings.PresencePenalty ??= AiProviderSettings.DefaultPresencePenalty;
        settings.RepetitionPenalty ??= AiProviderSettings.DefaultRepetitionPenalty;
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
        _contextLength = FormatNullableInt(settings.ContextLength);
        _temperature = FormatNullableDouble(settings.Temperature);
        _topP = FormatNullableDouble(settings.TopP);
        _topK = FormatNullableInt(settings.TopK);
        _minP = FormatNullableDouble(settings.MinP);
        _presencePenalty = FormatNullableDouble(settings.PresencePenalty);
        _repetitionPenalty = FormatNullableDouble(settings.RepetitionPenalty);
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

    public string TopP
    {
        get => _topP;
        set => SetProperty(ref _topP, value);
    }

    public string TopK
    {
        get => _topK;
        set => SetProperty(ref _topK, value);
    }

    public string MinP
    {
        get => _minP;
        set => SetProperty(ref _minP, value);
    }

    public string PresencePenalty
    {
        get => _presencePenalty;
        set => SetProperty(ref _presencePenalty, value);
    }

    public string RepetitionPenalty
    {
        get => _repetitionPenalty;
        set => SetProperty(ref _repetitionPenalty, value);
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
        Temperature = ParseNullableDouble(Temperature),
        TopP = ParseNullableDouble(TopP),
        TopK = ParseNullableInt(TopK),
        MinP = ParseNullableDouble(MinP),
        PresencePenalty = ParseNullableDouble(PresencePenalty),
        RepetitionPenalty = ParseNullableDouble(RepetitionPenalty)
    };

    private static string FormatNullableDouble(double? value)
    {
        return value.HasValue
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string FormatNullableInt(int? value)
    {
        return value.HasValue
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
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
