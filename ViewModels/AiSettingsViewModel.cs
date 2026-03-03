using KaneCode.Infrastructure;
using KaneCode.Services.Ai;
using System.Collections.ObjectModel;
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
            Entries.Add(new AiProviderEntryViewModel(s));
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
        "openai",
        "azure-openai",
        "llamacpp"
    ];

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
        // Ensure only one entry is marked active
        var activeFound = false;
        foreach (var entry in Entries)
        {
            if (entry.IsActive && !activeFound)
            {
                activeFound = true;
            }
            else if (entry.IsActive)
            {
                entry.IsActive = false;
            }
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
    private bool _isActive;

    public AiProviderEntryViewModel(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _providerId = settings.ProviderId;
        _label = settings.Label;
        _endpoint = settings.Endpoint;
        _apiKey = settings.ApiKey;
        _selectedModel = settings.SelectedModel;
        _isActive = settings.IsActive;
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

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// Converts this view model back to a settings model for persistence.
    /// </summary>
    public AiProviderSettings ToSettings() => new()
    {
        ProviderId = ProviderId,
        Label = Label,
        Endpoint = Endpoint,
        ApiKey = ApiKey,
        SelectedModel = SelectedModel,
        IsActive = IsActive
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? ProviderId : Label;
}
