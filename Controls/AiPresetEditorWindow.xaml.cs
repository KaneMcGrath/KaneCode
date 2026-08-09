using KaneCode.Models;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Modes;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// Master–detail window for creating and editing AI chat mode presets.
/// Left pane: searchable, filterable list of agent tools grouped by category.
/// Right pane: per-tool property editor (description, parameters, backend options,
/// tool definition) hosted by <see cref="ToolDetailView"/>.
/// </summary>
internal partial class AiPresetEditorWindow : Window
{
    private sealed class ToolRowVisual
    {
        public required Border Container { get; init; }

        public required CheckBox CheckBox { get; init; }

        public required TextBlock NameText { get; init; }

        public required TextBlock DescriptionText { get; init; }

        public required Border Badge { get; init; }

        public required TextBlock BadgeText { get; init; }

        public required Border AccentBar { get; init; }
    }

    private readonly AgentToolRegistry _toolRegistry;
    private readonly AiChatModeRegistry _modeRegistry;
    private readonly IAiChatMode? _activeMode;
    private readonly ObservableCollection<AiPreset> _presets = [];
    private readonly Dictionary<string, ToolEditState> _toolStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolRowVisual> _toolRows = new(StringComparer.Ordinal);

    private AiPreset? _currentPreset;
    private AiPreset? _revertSnapshot;
    private string? _selectedToolName;
    private string _activeFilter = "all";
    private DateTime? _lastSaved;
    private bool _suppressEvents;

    /// <summary>
    /// Resolves a theme brush by key. Accepts the editor's legacy "Brush.*" aliases
    /// and translates them to KaneCode theme keys so every lookup follows the
    /// MLib dark/light theme. Unknown keys are looked up as-is.
    /// </summary>
    private Brush Brush(string key) => (Brush)TryFindResource(TranslateBrushKey(key))!;

    private static string TranslateBrushKey(string key)
    {
        return key switch
        {
            "Brush.Amber" => "DiagnosticWarningForeground",
            "Brush.AmberSoftBg" => "AiChatToolCallBackground",
            "Brush.AmberSoftBorder" => "AiChatToolCallBorder",
            "Brush.Accent" => "ControlSelectionBackground",
            "Brush.AccentLight" => "DiagnosticInfoForeground",
            "Brush.AccentSoft" => "ControlBackground",
            "Brush.Text" => "WindowForeground",
            "Brush.Muted" => "PanelHeaderForeground",
            "Brush.Faint" => "ControlDisabledForeground",
            "Brush.PanelBg" => "PanelBackground",
            "Brush.InsetBg" => "EditorBackground",
            "Brush.Border" => "ControlBorder",
            "Brush.ControlBorder" => "ControlBorder",
            "Brush.Red" => "DiagnosticErrorForeground",
            "Brush.Green" => "GitAddedForeground",
            "Brush.SelectedRow" => "ControlSelectionBackground",
            "Brush.Disabled" => "ControlDisabledForeground",
            "Brush.WindowBg" => "WindowBackground",
            _ => key
        };
    }

    internal AiPresetEditorWindow(AgentToolRegistry toolRegistry, AiChatModeRegistry modeRegistry, IAiChatMode? activeMode, Window owner)
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(modeRegistry);

        _toolRegistry = toolRegistry;
        _modeRegistry = modeRegistry;
        _activeMode = activeMode;
        Owner = owner;
        InitializeComponent();

        LoadPresets();
        ShowTab("tools");
        SelectPreset(_presets.Count > 0 ? _presets[0] : null);
        RefreshPresetSelector();
        UpdateStatusBar();
    }

    /// <summary>
    /// Returns the list of presets after the editor is closed with DialogResult = true.
    /// </summary>
    internal IReadOnlyList<AiPreset> Presets => [.. _presets];

    private void LoadPresets()
    {
        _presets.Clear();
        foreach (AiPreset preset in AiPresetManager.Load())
        {
            _presets.Add(preset);
        }
    }

    // ── Tool edit states ───────────────────────────────────────────

    private void BuildToolStates(AiPreset? preset)
    {
        _toolStates.Clear();
        _toolRows.Clear();

        foreach (IAgentTool tool in _toolRegistry.Tools)
        {
            ToolEditState state = new(tool)
            {
                IsEnabled = preset?.AllowedTools is null || preset.AllowedTools.Contains(tool.Name)
            };

            if (preset?.ToolDescriptions is { } descriptions &&
                descriptions.TryGetValue(tool.Name, out string? descriptionOverride))
            {
                state.DescriptionOverride = descriptionOverride;
            }

            if (preset?.PinnedParameters is { } pinned &&
                pinned.TryGetValue(tool.Name, out Dictionary<string, JsonElement>? pinnedValues))
            {
                foreach ((string key, JsonElement value) in pinnedValues)
                {
                    state.PinnedParameters[key] = value.Clone();
                }
            }

            if (preset?.HiddenParameters is { } hidden &&
                hidden.TryGetValue(tool.Name, out HashSet<string>? hiddenNames))
            {
                foreach (string name in hiddenNames)
                {
                    state.HiddenParameters.Add(name);
                }
            }

            if (preset?.ToolOptions is { } options &&
                options.TryGetValue(tool.Name, out Dictionary<string, JsonElement>? optionOverrides))
            {
                foreach ((string key, JsonElement value) in optionOverrides)
                {
                    state.OptionOverrides[key] = value.Clone();
                }
            }

            _toolStates[tool.Name] = state;
        }
    }

    // ── Tool list ──────────────────────────────────────────────────

    private void RebuildToolsList()
    {
        ToolsListPanel.Children.Clear();
        _toolRows.Clear();

        string search = ToolSearchBox.Text?.Trim() ?? string.Empty;

        List<IGrouping<string, IAgentTool>> groups = _toolRegistry.Tools
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .GroupBy(t => t.Category ?? "General")
            .ToList();

        bool anyAdded = false;
        foreach (IGrouping<string, IAgentTool> group in groups)
        {
            List<IAgentTool> visible = group
                .Where(t => MatchesFilter(t, search))
                .ToList();
            if (visible.Count == 0)
            {
                continue;
            }

            if (anyAdded)
            {
                ToolsListPanel.Children.Add(new Separator
                {
                    Margin = new Thickness(10, 4, 10, 4),
                    Opacity = 0.4
                });
            }

            anyAdded = true;

            TextBlock header = new()
            {
                Text = $"{group.Key.ToUpperInvariant()}    {visible.Count}",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("Brush.Faint"),
                Margin = new Thickness(10, 4, 10, 2)
            };
            ToolsListPanel.Children.Add(header);

            foreach (IAgentTool tool in visible)
            {
                ToolsListPanel.Children.Add(CreateToolRow(tool));
            }
        }

        if (!anyAdded)
        {
            ToolsListPanel.Children.Add(new TextBlock
            {
                Text = "(no tools match)",
                FontSize = 11,
                Foreground = Brush("Brush.Faint"),
                Margin = new Thickness(10, 8, 10, 8)
            });
        }
    }

    private bool MatchesFilter(IAgentTool tool, string search)
    {
        ToolEditState state = _toolStates[tool.Name];

        switch (_activeFilter)
        {
            case "enabled":
                if (!state.IsEnabled)
                {
                    return false;
                }

                break;

            case "overridden":
                if (state.OverrideCount == 0)
                {
                    return false;
                }

                break;
        }

        if (search.Length == 0)
        {
            return true;
        }

        if (tool.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (tool.Category ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
            tool.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string paramName in AgentToolRegistry.GetParameterNames(tool))
        {
            if (paramName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (tool.BackendOptionsSchema.ValueKind == JsonValueKind.Object &&
            tool.BackendOptionsSchema.TryGetProperty("properties", out JsonElement props) &&
            props.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in props.EnumerateObject())
            {
                if (prop.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Border CreateToolRow(IAgentTool tool)
    {
        ToolEditState state = _toolStates[tool.Name];
        bool isSelected = string.Equals(tool.Name, _selectedToolName, StringComparison.Ordinal);

        Border container = new()
        {
            Background = isSelected ? Brush("Brush.SelectedRow") : Brushes.Transparent,
            Margin = new Thickness(4, 1, 4, 1),
            Cursor = Cursors.Hand,
            Tag = tool.Name
        };

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // accent
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // checkbox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // badge

        Border accentBar = new()
        {
            Width = 3,
            Background = Brush("Brush.Accent"),
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed
        };
        grid.Children.Add(accentBar);

        CheckBox checkBox = new()
        {
            IsChecked = state.IsEnabled,
            Tag = tool.Name,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 10, 4, 0),
            Foreground = Brush("Brush.Text")
        };
        checkBox.Checked += ToolCheckChanged;
        checkBox.Unchecked += ToolCheckChanged;
        Grid.SetColumn(checkBox, 1);
        grid.Children.Add(checkBox);

        StackPanel info = new() { Margin = new Thickness(4, 5, 4, 5) };
        TextBlock nameText = new()
        {
            Text = tool.Name,
            FontSize = 12.5,
            FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = state.IsEnabled
                ? Brush("Brush.Text")
                : Brush("Brush.Disabled")
        };
        info.Children.Add(nameText);

        TextBlock descText = new()
        {
            Text = tool.Description,
            FontSize = 10.5,
            Foreground = Brush("Brush.Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
            MaxWidth = 220
        };
        info.Children.Add(descText);
        Grid.SetColumn(info, 2);
        grid.Children.Add(info);

        Border badge = new()
        {
            Background = Brush("Brush.AmberSoftBg"),
            BorderBrush = Brush("Brush.AmberSoftBorder"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(4, 8, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = state.OverrideCount > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        TextBlock badgeText = new()
        {
            Text = state.OverrideCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Brush.Amber")
        };
        badge.Child = badgeText;
        Grid.SetColumn(badge, 3);
        grid.Children.Add(badge);

        container.Child = grid;
        container.MouseLeftButtonUp += (_, _) => SelectTool(tool.Name);

        _toolRows[tool.Name] = new ToolRowVisual
        {
            Container = container,
            CheckBox = checkBox,
            NameText = nameText,
            DescriptionText = descText,
            Badge = badge,
            BadgeText = badgeText,
            AccentBar = accentBar
        };

        return container;
    }

    private void RefreshRowVisuals()
    {
        foreach ((string name, ToolRowVisual row) in _toolRows)
        {
            if (!_toolStates.TryGetValue(name, out ToolEditState? state))
            {
                continue;
            }

            bool isSelected = string.Equals(name, _selectedToolName, StringComparison.Ordinal);

            _suppressEvents = true;
            try
            {
                row.CheckBox.IsChecked = state.IsEnabled;
            }
            finally
            {
                _suppressEvents = false;
            }

            row.NameText.Foreground = state.IsEnabled
                ? Brush("Brush.Text")
                : Brush("Brush.Disabled");
            row.NameText.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            row.Container.Background = isSelected
                ? Brush("Brush.SelectedRow")
                : Brushes.Transparent;
            row.AccentBar.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            row.Badge.Visibility = state.OverrideCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            row.BadgeText.Text = state.OverrideCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void ToolCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
        {
            return;
        }

        if (sender is CheckBox checkBox && checkBox.Tag is string name &&
            _toolStates.TryGetValue(name, out ToolEditState? state))
        {
            state.IsEnabled = checkBox.IsChecked == true;
            MarkDirty();
        }
    }

    private void SelectTool(string name)
    {
        _selectedToolName = name;

        if (_currentPreset is not null && _toolStates.TryGetValue(name, out ToolEditState? state))
        {
            ToolDetailHost.Visibility = Visibility.Visible;
            NoToolSelectedText.Visibility = Visibility.Collapsed;
            ToolDetailHost.Initialize(_toolRegistry, state, _currentPreset, MarkDirty);
        }
        else
        {
            ToolDetailHost.Visibility = Visibility.Collapsed;
            NoToolSelectedText.Visibility = Visibility.Visible;
        }

        RefreshRowVisuals();
    }

    // ── Preset lifecycle ───────────────────────────────────────────

    private void RefreshPresetSelector()
    {
        _suppressEvents = true;
        PresetSelector.ItemsSource = null;
        PresetSelector.ItemsSource = _presets;
        if (_currentPreset is not null && _presets.Contains(_currentPreset))
        {
            PresetSelector.SelectedItem = _currentPreset;
        }

        _suppressEvents = false;
    }

    private void SelectPreset(AiPreset? preset)
    {
        _currentPreset = preset;
        _revertSnapshot = preset?.Clone();
        _selectedToolName = null;

        _suppressEvents = true;
        PresetNameBox.Text = preset?.Name ?? string.Empty;
        SystemPromptBox.Text = preset?.SystemPrompt ?? string.Empty;
        DefaultPresetCheckBox.IsChecked = preset?.IsDefault == true;
        _suppressEvents = false;

        BuildToolStates(preset);

        bool editable = preset is not null;
        ToolsMainPanel.IsEnabled = editable;
        SystemPromptPanel.IsEnabled = editable;
        DefaultPresetCheckBox.IsEnabled = editable;

        if (editable && _toolStates.Count > 0)
        {
            SelectTool(_toolStates.Keys.OrderBy(k => k, StringComparer.Ordinal).First());
        }
        else
        {
            ToolDetailHost.Visibility = Visibility.Collapsed;
            NoToolSelectedText.Visibility = Visibility.Visible;
        }

        RebuildToolsList();
        SaveButton.IsEnabled = false;
        RevertButton.IsEnabled = false;
        UpdateStatusBar();
    }

    private void MarkDirty()
    {
        if (_suppressEvents || _currentPreset is null)
        {
            return;
        }

        SaveButton.IsEnabled = true;
        RevertButton.IsEnabled = true;
        RefreshRowVisuals();
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        int total = _toolRegistry.Tools.Count();
        int enabled = _toolStates.Values.Count(s => s.IsEnabled);
        int customizedOptions = _toolStates.Values.Sum(s => s.OptionOverrides.Count);
        int hiddenParams = _toolStates.Values.Sum(s => s.HiddenParameters.Count);
        int customizedTotal = _toolStates.Values.Sum(s => s.OverrideCount);

        EnabledPillText.Text = $"{enabled} / {total} tools enabled";
        ListCountText.Text = $"{enabled} / {total} enabled";

        string status;
        if (customizedTotal == 0 && enabled == total)
        {
            status = "Schema valid — no overrides · all tools enabled";
        }
        else
        {
            status = $"Schema valid — {customizedOptions} backend option{(customizedOptions == 1 ? "" : "s")} customized · " +
                     $"{hiddenParams} hidden param{(hiddenParams == 1 ? "" : "s")} · {enabled}/{total} tools enabled";
        }

        StatusText.Text = status;

        AiPreset? defaultPreset = _presets.FirstOrDefault(p => p.IsDefault);
        if (defaultPreset is not null)
        {
            StatusText.Text += $" · Default agent mode: {defaultPreset.Name}";
        }

        if (_lastSaved is DateTime saved)
        {
            TimeSpan ago = DateTime.Now - saved;
            string agoText = ago.TotalMinutes < 1
                ? "just now"
                : ago.TotalMinutes < 60
                    ? $"{(int)ago.TotalMinutes} min ago"
                    : $"{(int)ago.TotalHours} hr ago";
            LastSavedText.Text = $"Last saved {agoText} · ai-presets.json";
        }
        else
        {
            LastSavedText.Text = "Not saved yet · ai-presets.json";
        }
    }

    private void ApplyStateToPreset()
    {
        if (_currentPreset is null)
        {
            return;
        }

        _currentPreset.Name = PresetNameBox.Text;
        _currentPreset.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;

        int enabledCount = _toolStates.Values.Count(s => s.IsEnabled);
        _currentPreset.AllowedTools = enabledCount == _toolStates.Count
            ? null
            : new HashSet<string>(_toolStates.Values.Where(s => s.IsEnabled).Select(s => s.Tool.Name), StringComparer.Ordinal);

        Dictionary<string, string> descriptions = _toolStates.Values
            .Where(s => s.DescriptionModified)
            .ToDictionary(s => s.Tool.Name, s => s.DescriptionOverride!, StringComparer.Ordinal);
        _currentPreset.ToolDescriptions = descriptions.Count == 0 ? null : descriptions;

        Dictionary<string, Dictionary<string, JsonElement>> pinned = _toolStates.Values
            .Where(s => s.PinnedParameters.Count > 0)
            .ToDictionary(
                s => s.Tool.Name,
                s => new Dictionary<string, JsonElement>(s.PinnedParameters, StringComparer.Ordinal),
                StringComparer.Ordinal);
        _currentPreset.PinnedParameters = pinned.Count == 0 ? null : pinned;

        Dictionary<string, Dictionary<string, JsonElement>> options = _toolStates.Values
            .Where(s => s.OptionOverrides.Count > 0)
            .ToDictionary(
                s => s.Tool.Name,
                s => new Dictionary<string, JsonElement>(s.OptionOverrides, StringComparer.Ordinal),
                StringComparer.Ordinal);
        _currentPreset.ToolOptions = options.Count == 0 ? null : options;

        Dictionary<string, HashSet<string>> hidden = _toolStates.Values
            .Where(s => s.HiddenParameters.Count > 0)
            .ToDictionary(
                s => s.Tool.Name,
                s => new HashSet<string>(s.HiddenParameters, StringComparer.Ordinal),
                StringComparer.Ordinal);
        _currentPreset.HiddenParameters = hidden.Count == 0 ? null : hidden;
    }

    // ── Header actions ─────────────────────────────────────────────

    private void PresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (PresetSelector.SelectedItem is AiPreset preset)
        {
            SelectPreset(preset);
        }
    }

    private void NewPresetButton_Click(object sender, RoutedEventArgs e)
    {
        AiPreset newPreset = new() { Name = "New Preset" };
        _presets.Add(newPreset);
        RefreshPresetSelector();
        PresetSelector.SelectedItem = newPreset;
        SelectPreset(newPreset);
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
        SaveButton.IsEnabled = true;
    }

    private void CopyFromToggleButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateCopyFromDropdown();
        CopyFromPopup.IsOpen = true;
    }

    private void CopyFromPopup_Closed(object sender, EventArgs e)
    {
    }

    private void PopulateCopyFromDropdown()
    {
        CopyFromItemsPanel.Children.Clear();

        var foreBrush = Brush("Brush.Text");
        var backBrush = Brush("Brush.InsetBg");
        var borderBrush = Brush("Brush.ControlBorder");
        var hoverBrush = Brush("Brush.PanelBg");

        if (_activeMode is not null)
        {
            Border activeBorder = CreateDropdownItem(
                "Current: " + _activeMode.DisplayName, foreBrush, backBrush, borderBrush, hoverBrush, isBold: true,
                toolTip: "Copy settings from the currently active chat mode");
            activeBorder.MouseLeftButtonUp += (_, _) =>
            {
                CopyFromMode(_activeMode);
                CopyFromPopup.IsOpen = false;
            };
            CopyFromItemsPanel.Children.Add(activeBorder);
        }

        CopyFromItemsPanel.Children.Add(CreateCategoryHeader("Built-in", foreBrush, backBrush, borderBrush));
        foreach (IAiChatMode mode in _modeRegistry.Modes)
        {
            if (_activeMode is not null && mode.Id == _activeMode.Id)
            {
                continue;
            }

            Border modeBorder = CreateDropdownItem(mode.DisplayName, foreBrush, backBrush, borderBrush, hoverBrush, isBold: false,
                toolTip: "Copy settings from this built-in mode");
            IAiChatMode captured = mode;
            modeBorder.MouseLeftButtonUp += (_, _) =>
            {
                CopyFromMode(captured);
                CopyFromPopup.IsOpen = false;
            };
            CopyFromItemsPanel.Children.Add(modeBorder);
        }

        CopyFromItemsPanel.Children.Add(CreateCategoryHeader("Presets", foreBrush, backBrush, borderBrush));
        if (_presets.Count > 0)
        {
            foreach (AiPreset preset in _presets)
            {
                PresetMode presetMode = new(preset, _toolRegistry);
                if (_activeMode is not null && presetMode.Id == _activeMode.Id)
                {
                    continue;
                }

                Border presetBorder = CreateDropdownItem(preset.Name, foreBrush, backBrush, borderBrush, hoverBrush, isBold: false,
                    toolTip: "Copy settings from this user-created preset");
                AiPreset capturedPreset = preset;
                presetBorder.MouseLeftButtonUp += (_, _) =>
                {
                    CopyFromPreset(capturedPreset);
                    CopyFromPopup.IsOpen = false;
                };
                CopyFromItemsPanel.Children.Add(presetBorder);
            }
        }
        else
        {
            CopyFromItemsPanel.Children.Add(new TextBlock
            {
                Text = "(no presets yet)",
                Foreground = foreBrush,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Opacity = 0.6,
                Padding = new Thickness(10, 5, 10, 5)
            });
        }
    }

    private void CopyFromMode(IAiChatMode sourceMode)
    {
        string? systemPrompt = sourceMode.BuildSystemPrompt(default);
        HashSet<string>? allowedTools = sourceMode.AllowedTools is not null
            ? new HashSet<string>(sourceMode.AllowedTools, StringComparer.Ordinal)
            : null;

        AiPreset newPreset = new()
        {
            Name = $"{sourceMode.DisplayName} (Copy)",
            SystemPrompt = systemPrompt,
            AllowedTools = allowedTools
        };

        AddAndSelectNewPreset(newPreset);
    }

    private void CopyFromPreset(AiPreset source)
    {
        AiPreset clone = source.Clone();
        AiPreset newPreset = new()
        {
            Name = $"{source.Name} (Copy)",
            SystemPrompt = source.SystemPrompt,
            AllowedTools = source.AllowedTools is null ? null : new HashSet<string>(source.AllowedTools, StringComparer.Ordinal),
            ToolDescriptions = clone.ToolDescriptions,
            PinnedParameters = clone.PinnedParameters,
            ToolOptions = clone.ToolOptions,
            HiddenParameters = clone.HiddenParameters
        };

        AddAndSelectNewPreset(newPreset);
    }

    private void AddAndSelectNewPreset(AiPreset newPreset)
    {
        _presets.Add(newPreset);
        RefreshPresetSelector();
        PresetSelector.SelectedItem = newPreset;
        SelectPreset(newPreset);
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
        SaveButton.IsEnabled = true;
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Delete preset \"{_currentPreset.Name}\"?",
            "Delete Preset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _presets.Remove(_currentPreset);
        _currentPreset = null;
        SelectPreset(_presets.Count > 0 ? _presets[0] : null);
        RefreshPresetSelector();
        MarkDirty();
    }

    private void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null || _revertSnapshot is null)
        {
            return;
        }

        // Restore fields from the snapshot.
        _currentPreset.Name = _revertSnapshot.Name;
        _currentPreset.SystemPrompt = _revertSnapshot.SystemPrompt;
        _currentPreset.IsDefault = _revertSnapshot.IsDefault;
        _currentPreset.AllowedTools = _revertSnapshot.AllowedTools is null
            ? null
            : new HashSet<string>(_revertSnapshot.AllowedTools, StringComparer.Ordinal);
        _currentPreset.ToolDescriptions = _revertSnapshot.ToolDescriptions is null
            ? null
            : new Dictionary<string, string>(_revertSnapshot.ToolDescriptions, StringComparer.Ordinal);
        _currentPreset.PinnedParameters = _revertSnapshot.PinnedParameters is null
            ? null
            : _revertSnapshot.Clone().PinnedParameters;
        _currentPreset.ToolOptions = _revertSnapshot.ToolOptions is null
            ? null
            : _revertSnapshot.Clone().ToolOptions;
        _currentPreset.HiddenParameters = _revertSnapshot.HiddenParameters is null
            ? null
            : _revertSnapshot.Clone().HiddenParameters;

        SelectPreset(_currentPreset);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
        {
            return;
        }

        ApplyStateToPreset();
        AiPresetManager.Save(_presets);
        _revertSnapshot = _currentPreset.Clone();
        _lastSaved = DateTime.Now;
        SaveButton.IsEnabled = false;
        RevertButton.IsEnabled = false;
        UpdateStatusBar();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PresetNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
        {
            return;
        }

        _currentPreset.Name = PresetNameBox.Text;
        SaveButton.IsEnabled = true;
    }

    private void SystemPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
        {
            return;
        }

        _currentPreset.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        MarkDirty();
    }

    private void DefaultPresetCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _currentPreset is null)
        {
            return;
        }

        if (DefaultPresetCheckBox.IsChecked == true)
        {
            // Only one preset can be the default — clear the flag on every other preset.
            foreach (AiPreset preset in _presets)
            {
                preset.IsDefault = ReferenceEquals(preset, _currentPreset);
            }
        }
        else
        {
            _currentPreset.IsDefault = false;
        }

        MarkDirty();
        RefreshPresetSelector();
    }

    // ── Search / filter / select all ───────────────────────────────

    private void ToolSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RebuildToolsList();
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string filter)
        {
            _activeFilter = filter;
            UpdateFilterChipVisuals();
            RebuildToolsList();
        }
    }

    private void UpdateFilterChipVisuals()
    {
        SetChipActive(FilterAllChip, _activeFilter == "all");
        SetChipActive(FilterEnabledChip, _activeFilter == "enabled");
        SetChipActive(FilterOverriddenChip, _activeFilter == "overridden");
    }

    private void SetChipActive(Button chip, bool active)
    {
        chip.Background = active
            ? Brush("Brush.Accent")
            : Brush("Brush.PanelBg");
        chip.Foreground = active ? Brushes.White : Brush("Brush.Text");
        chip.BorderBrush = active
            ? Brush("Brush.Accent")
            : Brush("Brush.ControlBorder");
    }

    private void SelectAllLink_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
        {
            return;
        }

        foreach (ToolEditState state in _toolStates.Values)
        {
            state.IsEnabled = true;
        }

        MarkDirty();
    }

    // ── Tabs ───────────────────────────────────────────────────────

    private void ShowTab(string tab)
    {
        ToolsMainPanel.Visibility = tab == "tools" ? Visibility.Visible : Visibility.Collapsed;
        SystemPromptPanel.Visibility = tab == "prompt" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanel.Visibility = tab == "advanced" ? Visibility.Visible : Visibility.Collapsed;

        SetTabActive(SystemPromptTabButton, tab == "prompt");
        SetTabActive(ToolsTabButton, tab == "tools");
        SetTabActive(AdvancedTabButton, tab == "advanced");

        if (tab == "advanced")
        {
            AdvancedSummaryText.Text = BuildAdvancedSummary();
        }
    }

    private string BuildAdvancedSummary()
    {
        int total = _toolRegistry.Tools.Count();
        int overriddenTools = _toolStates.Values.Count(s => s.OverrideCount > 0);
        int pinnedParams = _toolStates.Values.Sum(s => s.PinnedParameters.Count);
        int hiddenParams = _toolStates.Values.Sum(s => s.HiddenParameters.Count);
        int backendOptions = _toolStates.Values.Sum(s => s.OptionOverrides.Count);

        return
            $"Schema version: 3 (ai-presets.json)\n" +
            $"Registered tools: {total}\n" +
            $"Tools with overrides: {overriddenTools}\n" +
            $"Pinned parameters: {pinnedParams}\n" +
            $"Hidden parameters: {hiddenParams}\n" +
            $"Backend options customized: {backendOptions}\n\n" +
            "Per-tool overrides are stored in the preset: description overrides, " +
            "pinned parameter values (merged into the model-facing schema as defaults), " +
            "hidden parameters (removed from the model-facing schema so the agent " +
            "never sees them), and backend options (execution configuration that is " +
            "never sent to the model). " +
            "Tool definitions for the model are resolved at runtime by AgentToolRegistry.";
    }

    private void SetTabActive(Button button, bool active)
    {
        button.Foreground = active ? Brush("WindowForeground") : Brush("PanelHeaderForeground");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void SystemPromptTab_Click(object sender, RoutedEventArgs e) => ShowTab("prompt");

    private void ToolsTab_Click(object sender, RoutedEventArgs e) => ShowTab("tools");

    private void AdvancedTab_Click(object sender, RoutedEventArgs e) => ShowTab("advanced");

    // ── Keyboard navigation ────────────────────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F)
        {
            ToolSearchBox.Focus();
            ToolSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            List<string> visible = _toolRows.Keys.ToList();
            if (visible.Count == 0)
            {
                return;
            }

            int index = _selectedToolName is null ? -1 : visible.IndexOf(_selectedToolName);
            int next = e.Key == Key.Down
                ? Math.Min(visible.Count - 1, index + 1)
                : Math.Max(0, index - 1);

            if (index < 0 && e.Key == Key.Down)
            {
                next = 0;
            }

            SelectTool(visible[next]);
            e.Handled = true;
        }
    }

    // ── Copy From dropdown helpers ─────────────────────────────────

    private static Border CreateDropdownItem(
        string text, Brush foreBrush, Brush backBrush, Brush borderBrush, Brush? hoverBrush, bool isBold, string? toolTip)
    {
        Border border = new()
        {
            Padding = new Thickness(10, 5, 10, 5),
            Background = backBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
            ToolTip = toolTip
        };

        border.Child = new TextBlock
        {
            Text = text,
            Foreground = foreBrush,
            FontSize = 12,
            FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal
        };

        if (hoverBrush is not null)
        {
            border.MouseEnter += (_, _) => border.Background = hoverBrush;
            border.MouseLeave += (_, _) => border.Background = backBrush;
        }

        return border;
    }

    private static Border CreateCategoryHeader(string categoryText, Brush foreBrush, Brush backBrush, Brush borderBrush)
    {
        Border container = new() { Background = backBrush };
        StackPanel stack = new();
        container.Child = stack;

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = borderBrush,
            Opacity = 0.3,
            Margin = new Thickness(0, 4, 0, 0)
        });

        stack.Children.Add(new TextBlock
        {
            Text = categoryText,
            Foreground = foreBrush,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.55,
            Padding = new Thickness(10, 4, 10, 3)
        });

        return container;
    }
}
