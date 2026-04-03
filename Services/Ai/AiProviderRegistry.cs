namespace KaneCode.Services.Ai;

/// <summary>
/// Resolves <see cref="IAiProvider"/> instances from persisted <see cref="AiProviderSettings"/>.
/// Call <see cref="Reload"/> after settings change to pick up new configuration.
/// </summary>
internal sealed class AiProviderRegistry : IDisposable
{
    private readonly List<IAiProvider> _providers = [];
    private readonly Dictionary<IAiProvider, AiProviderSettings> _settingsMap = [];
    private IAiProvider? _activeProvider;

    public AiProviderRegistry()
    {
        AiSettingsManager.SettingsSaved += OnSettingsSaved;
    }

    internal event EventHandler? ProvidersChanged;

    /// <summary>
    /// All currently registered provider instances.
    /// </summary>
    public IReadOnlyList<IAiProvider> Providers => _providers;

    /// <summary>
    /// The provider marked as active/default, or null if none is configured.
    /// </summary>
    public IAiProvider? ActiveProvider => _activeProvider;

    /// <summary>
    /// Sets the active provider to the specified instance.
    /// The provider must already be registered.
    /// </summary>
    public void SetActiveProvider(IAiProvider? provider)
    {
        _activeProvider = provider;
    }

    /// <summary>
    /// Loads (or reloads) providers from the persisted AI settings.
    /// Disposes any previously created provider instances.
    /// </summary>
    public void Reload()
    {
        int activeProviderIndex = _activeProvider is null ? -1 : _providers.IndexOf(_activeProvider);
        DisposeProviders();

        List<AiProviderSettings> settings = AiSettingsManager.Load();

        foreach (AiProviderSettings s in settings)
        {
            IAiProvider? provider = CreateProvider(s);
            if (provider is null)
            {
                continue;
            }

            _providers.Add(provider);
            _settingsMap[provider] = s;
        }

        if (activeProviderIndex >= 0 && activeProviderIndex < _providers.Count)
        {
            _activeProvider = _providers[activeProviderIndex];
        }

        if (_activeProvider is null || !_activeProvider.IsConfigured)
        {
            _activeProvider = _providers.FirstOrDefault(p => p.IsConfigured);
        }

        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates an <see cref="IAiProvider"/> instance for the given settings, or null
    /// if the provider ID is not recognized.
    /// </summary>
    private static IAiProvider? CreateProvider(AiProviderSettings settings)
    {
        return settings.ProviderId switch
        {
            "openai" => new OpenAiProvider(settings),
            "llamacpp" => new OpenAiProvider(settings),
            // Future: "azure-openai" => new AzureOpenAiProvider(settings),
            _ => null
        };
    }

    /// <summary>
    /// Returns the persisted settings for the given provider, or null if not found.
    /// </summary>
    public AiProviderSettings? GetSettings(IAiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _settingsMap.GetValueOrDefault(provider);
    }

    private void DisposeProviders()
    {
        foreach (IAiProvider provider in _providers)
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _providers.Clear();
        _settingsMap.Clear();
        _activeProvider = null;
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        Reload();
    }

    public void Dispose()
    {
        AiSettingsManager.SettingsSaved -= OnSettingsSaved;
        DisposeProviders();
    }
}
