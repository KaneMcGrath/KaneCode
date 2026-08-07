using KaneCode.Infrastructure;
using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Models;

/// <summary>
/// Mutable per-tool edit state used by the preset editor while a preset is open.
/// Wraps an <see cref="IAgentTool"/> together with the preset's overrides for it
/// (enabled flag, description override, pinned parameter values, backend options).
/// The window serializes these back into <see cref="AiPreset"/> on Save.
/// </summary>
internal sealed class ToolEditState : ObservableObject
{
    private bool _isEnabled = true;
    private string? _descriptionOverride;

    public ToolEditState(IAgentTool tool)
    {
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
    }

    /// <summary>The underlying tool definition (immutable schema + defaults).</summary>
    public IAgentTool Tool { get; }

    /// <summary>Whether the tool is enabled (checked) for the current preset.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(OverrideCount));
                OnPropertyChanged(nameof(HasOverrides));
            }
        }
    }

    /// <summary>
    /// The description override (null = use the tool's canonical description).
    /// </summary>
    public string? DescriptionOverride
    {
        get => _descriptionOverride;
        set
        {
            if (SetProperty(ref _descriptionOverride, value))
            {
                OnPropertyChanged(nameof(OverrideCount));
                OnPropertyChanged(nameof(HasOverrides));
                OnPropertyChanged(nameof(DescriptionModified));
            }
        }
    }

    /// <summary>Pinned parameter values: parameter name → locked value.</summary>
    public Dictionary<string, JsonElement> PinnedParameters { get; } = new(StringComparer.Ordinal);

    /// <summary>Backend option overrides: option name → overridden value.</summary>
    public Dictionary<string, JsonElement> OptionOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the description differs from the tool's default.</summary>
    public bool DescriptionModified => !string.IsNullOrWhiteSpace(DescriptionOverride)
        && !string.Equals(DescriptionOverride, Tool.Description, StringComparison.Ordinal);

    /// <summary>True when any override exists for this tool.</summary>
    public bool HasOverrides => OverrideCount > 0;

    /// <summary>
    /// Number of overrides for this tool: description (1) + pinned params + backend options.
    /// Feeds the amber badge in the tool list and the "Overridden" filter.
    /// </summary>
    public int OverrideCount
    {
        get
        {
            int count = PinnedParameters.Count + OptionOverrides.Count;
            if (!string.IsNullOrWhiteSpace(DescriptionOverride))
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>Pins a parameter value; fires change notifications for the badge.</summary>
    public void SetPinnedParameter(string name, JsonElement value)
    {
        PinnedParameters[name] = value.Clone();
        NotifyOverridesChanged();
    }

    /// <summary>Unpins a parameter value; fires change notifications for the badge.</summary>
    public void RemovePinnedParameter(string name)
    {
        if (PinnedParameters.Remove(name))
        {
            NotifyOverridesChanged();
        }
    }

    /// <summary>Overrides a backend option; fires change notifications for the badge.</summary>
    public void SetOptionOverride(string name, JsonElement value)
    {
        OptionOverrides[name] = value.Clone();
        NotifyOverridesChanged();
    }

    /// <summary>Restores a backend option to its tool default; fires change notifications.</summary>
    public void RemoveOptionOverride(string name)
    {
        if (OptionOverrides.Remove(name))
        {
            NotifyOverridesChanged();
        }
    }

    private void NotifyOverridesChanged()
    {
        OnPropertyChanged(nameof(OverrideCount));
        OnPropertyChanged(nameof(HasOverrides));
    }
}
