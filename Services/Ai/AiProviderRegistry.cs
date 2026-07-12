using System;
using System.Collections.Generic;
using System.Linq;

namespace KaneCode.Services.Ai;

/// <summary>
/// Resolves <see cref="IAiProvider"/> instances from persisted <see cref="AiProviderSettings"/>.
/// Call <see cref="Reload"/> after settings change to pick up new configuration.
/// The active provider selection is persisted via the <see cref="AiProviderSettings.IsActive"/> flag
/// so it survives application restarts.
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
    /// Sets the active provider to the specified instance and persists the selection
    /// to disk so it survives application restarts. The provider must already be registered.
    /// </summary>
    public void SetActiveProvider(IAiProvider? provider)
    {
        _activeProvider = provider;

        // Persist the active provider selection by updating the IsActive flags
        PersistActiveProviderFlag();
    }

    /// <summary>
    /// Loads (or reloads) providers from the persisted AI settings.
    /// Disposes any previously created provider instances.
    /// Restores the active provider from the persisted <see cref="AiProviderSettings.IsActive"/> flag.
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

        // Restore active provider from persisted IsActive flag (preferred on fresh load)
        AiProviderSettings? activeSettings = settings.FirstOrDefault(s => s.IsActive);
        if (activeSettings is not null)
        {
            _activeProvider = _providers.FirstOrDefault(p =>
                ReferenceEquals(_settingsMap.GetValueOrDefault(p), activeSettings));
        }

        // Fallback to index-based restore for in-session reloads
        if (_activeProvider is null && activeProviderIndex >= 0 && activeProviderIndex < _providers.Count)
        {
            _activeProvider = _providers[activeProviderIndex];
        }

        // Final fallback: first configured provider
        if (_activeProvider is null || !_activeProvider.IsConfigured)
        {
            _activeProvider = _providers.FirstOrDefault(p => p.IsConfigured);
        }

        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Persists the currently active provider by updating the <see cref="AiProviderSettings.IsActive"/>
    /// flag across all registered providers and saving to disk.
    /// </summary>
    private void PersistActiveProviderFlag()
    {
        foreach (AiProviderSettings s in _settingsMap.Values)
        {
            s.IsActive = false;
        }

        if (_activeProvider is not null && _settingsMap.TryGetValue(_activeProvider, out AiProviderSettings? activeSettings))
        {
            activeSettings.IsActive = true;
        }

        AiSettingsManager.Save(_settingsMap.Values.ToList(), raiseEvent: false);
    }

    /// <summary>
    /// Creates an <see cref="IAiProvider"/> instance for the given settings, or null
    /// if the provider ID is not recognized.
    /// </summary>
    private static IAiProvider? CreateProvider(AiProviderSettings settings)
    {
        return settings.ProviderId switch
        {
            "v1completions" => new V1CompletionsProvider(settings),
            "llamacpp" => new V1CompletionsProvider(settings),
            // Future: "v1chatcompletions" => new V1ChatCompletionsProvider(settings),
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
