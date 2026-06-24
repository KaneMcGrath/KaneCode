using KaneCode.Models;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Modes;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

/// <summary>
/// Window for creating and editing AI chat mode presets.
/// Users can select existing presets or create new ones, and configure
/// the system prompt and allowed tools for each preset.
/// </summary>
internal partial class AiPresetEditorWindow : Window
{
    private readonly AgentToolRegistry _toolRegistry;
    private readonly AiChatModeRegistry _modeRegistry;
    private readonly IAiChatMode? _activeMode;
    private readonly ObservableCollection<AiPreset> _presets = [];
    private AiPreset? _currentPreset;
    private bool _suppressEvents;

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
        SetupToolsCheckboxes();
        RefreshPresetSelector();
    }

    /// <summary>
    /// Returns the list of presets after the editor is closed with DialogResult = true.
    /// The caller should persist these presets via <see cref="AiPresetManager.Save"/>.
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

    private void SetupToolsCheckboxes()
    {
        ToolsPanel.Children.Clear();

        if (!_toolRegistry.HasTools)
        {
            TextBlock noTools = new()
            {
                Text = "(no tools registered)",
                FontSize = 11,
                Foreground = FindResource("EditorForeground") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray
            };
            ToolsPanel.Children.Add(noTools);
            return;
        }

        // Group tools by category
        Dictionary<string, List<IAgentTool>> groups = [];
        List<string> groupOrder = [];

        foreach (IAgentTool tool in _toolRegistry.Tools)
        {
            string category = tool.Category ?? "General";
            if (!groups.ContainsKey(category))
            {
                groups[category] = [];
                groupOrder.Add(category);
            }
            groups[category].Add(tool);
        }

        // Sort tools within each group
        foreach (List<IAgentTool> list in groups.Values)
        {
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }

        bool isFirstGroup = true;
        foreach (string category in groupOrder)
        {
            List<IAgentTool> groupTools = groups[category];
            if (groupTools.Count == 0)
            {
                continue;
            }

            if (!isFirstGroup)
            {
                Separator separator = new()
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Opacity = 0.4
                };
                ToolsPanel.Children.Add(separator);
            }
            isFirstGroup = false;

            TextBlock groupHeader = new()
            {
                Text = category,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = FindResource("EditorForeground") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray
            };
            ToolsPanel.Children.Add(groupHeader);

            foreach (IAgentTool tool in groupTools)
            {
                CheckBox checkBox = new()
                {
                    Content = tool.Name,
                    IsChecked = true, // default: enabled
                    Tag = tool.Name,
                    Margin = new Thickness(16, 0, 0, 2),
                    FontSize = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, Courier New"),
                    Foreground = FindResource("EditorForeground") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray,
                    ToolTip = tool.Description
                };

                checkBox.Checked += (_, _) => MarkDirty();
                checkBox.Unchecked += (_, _) => MarkDirty();
                ToolsPanel.Children.Add(checkBox);
            }
        }
    }

    private void RefreshPresetSelector()
    {
        _suppressEvents = true;
        PresetSelector.ItemsSource = null;
        PresetSelector.ItemsSource = _presets;

        if (_currentPreset is not null && _presets.Contains(_currentPreset))
        {
            PresetSelector.SelectedItem = _currentPreset;
        }
        else if (_presets.Count > 0)
        {
            PresetSelector.SelectedIndex = 0;
            SelectPreset(_presets[0]);
        }
        else
        {
            SelectPreset(null);
        }

        _suppressEvents = false;
    }

    private void SelectPreset(AiPreset? preset)
    {
        _currentPreset = preset;
        _suppressEvents = true;

        if (preset is not null)
        {
            PresetNameBox.Text = preset.Name;
            SystemPromptBox.Text = preset.SystemPrompt ?? string.Empty;
            ApplyToolsState(preset.AllowedTools);
            SaveButton.IsEnabled = true;
        }
        else
        {
            PresetNameBox.Text = string.Empty;
            SystemPromptBox.Text = string.Empty;
            ResetToolsState();
            SaveButton.IsEnabled = false;
        }

        _suppressEvents = false;
    }

    private void ApplyToolsState(HashSet<string>? allowedTools)
    {
        foreach (UIElement element in ToolsPanel.Children)
        {
            if (element is CheckBox checkBox && checkBox.Tag is string toolName)
            {
                checkBox.IsChecked = allowedTools is null || allowedTools.Contains(toolName);
            }
        }
    }

    private void ResetToolsState()
    {
        foreach (UIElement element in ToolsPanel.Children)
        {
            if (element is CheckBox checkBox)
            {
                checkBox.IsChecked = false;
            }
        }
    }

    private HashSet<string>? GetSelectedTools()
    {
        HashSet<string> enabledTools = [];

        foreach (UIElement element in ToolsPanel.Children)
        {
            if (element is CheckBox checkBox && checkBox.Tag is string toolName && checkBox.IsChecked == true)
            {
                enabledTools.Add(toolName);
            }
        }

        // If all tools are enabled, return null (unrestricted)
        if (enabledTools.Count == _toolRegistry.Tools.Count())
        {
            return null;
        }

        return enabledTools;
    }

    private void MarkDirty()
    {
        if (!_suppressEvents && _currentPreset is not null)
        {
            SaveButton.IsEnabled = true;
        }
    }

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
        AiPreset newPreset = new()
        {
            Name = "New Preset"
        };

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
        // Build the dropdown items each time the button is clicked
        PopulateCopyFromDropdown();

        // Open the popup below the toggle button
        CopyFromPopup.IsOpen = true;
        CopyFromToggleButton.IsChecked = true;
    }

    private void CopyFromPopup_Closed(object sender, EventArgs e)
    {
        CopyFromToggleButton.IsChecked = false;
    }

    /// <summary>
    /// Populates the dropdown items in the CopyFromPopup with all
    /// available modes and presets to copy from.
    /// </summary>
    private void PopulateCopyFromDropdown()
    {
        CopyFromItemsPanel.Children.Clear();

        var foreBrush = TryFindResource("EditorForeground") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        var backBrush = TryFindResource("EditorBackground") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;
        var borderBrush = TryFindResource("EditorBorder") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
        var hoverBrush = TryFindResource("ButtonMouseOverBackground") as System.Windows.Media.Brush;

        // 1. Currently active mode at the top (if any)
        if (_activeMode is not null)
        {
            Border activeBorder = CreateDropdownItem(
                "Current: " + _activeMode.DisplayName,
                foreBrush,
                backBrush,
                borderBrush,
                hoverBrush,
                isBold: true,
                toolTip: "Copy settings from the currently active chat mode");
            activeBorder.MouseLeftButtonUp += (_, _) =>
            {
                CopyFromMode(_activeMode);
                CopyFromPopup.IsOpen = false;
            };
            CopyFromItemsPanel.Children.Add(activeBorder);
        }

        // 2. Category header for built-in modes
        CopyFromItemsPanel.Children.Add(CreateCategoryHeader("Built-in", foreBrush, backBrush, borderBrush));

        foreach (IAiChatMode mode in _modeRegistry.Modes)
        {
            if (_activeMode is not null && mode.Id == _activeMode.Id)
            {
                continue;
            }

            Border modeBorder = CreateDropdownItem(
                mode.DisplayName,
                foreBrush,
                backBrush,
                borderBrush,
                hoverBrush,
                isBold: false,
                toolTip: "Copy settings from this built-in mode");
            IAiChatMode capturedMode = mode;
            modeBorder.MouseLeftButtonUp += (_, _) =>
            {
                CopyFromMode(capturedMode);
                CopyFromPopup.IsOpen = false;
            };
            CopyFromItemsPanel.Children.Add(modeBorder);
        }

        // 3. Category header + user-created presets
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

                Border presetBorder = CreateDropdownItem(
                    preset.Name,
                    foreBrush,
                    backBrush,
                    borderBrush,
                    hoverBrush,
                    isBold: false,
                    toolTip: "Copy settings from this user-created preset");
                PresetMode capturedPresetMode = presetMode;
                presetBorder.MouseLeftButtonUp += (_, _) =>
                {
                    CopyFromMode(capturedPresetMode);
                    CopyFromPopup.IsOpen = false;
                };
                CopyFromItemsPanel.Children.Add(presetBorder);
            }
        }
        else
        {
            // No presets exist yet — show a hint
            TextBlock noPresetsHint = new()
            {
                Text = "(no presets yet)",
                Foreground = foreBrush,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Opacity = 0.6,
                Padding = new Thickness(10, 5, 10, 5)
            };
            CopyFromItemsPanel.Children.Add(noPresetsHint);
        }
    }

    /// <summary>
    /// Creates a styled Border element that looks like a dropdown list item.
    /// </summary>
    private static Border CreateDropdownItem(
        string text,
        System.Windows.Media.Brush foreBrush,
        System.Windows.Media.Brush backBrush,
        System.Windows.Media.Brush borderBrush,
        System.Windows.Media.Brush? hoverBrush,
        bool isBold,
        string? toolTip)
    {
        Border border = new()
        {
            Padding = new Thickness(10, 5, 10, 5),
            Background = backBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = toolTip
        };

        TextBlock textBlock = new()
        {
            Text = text,
            Foreground = foreBrush,
            FontSize = 12,
            FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal
        };
        border.Child = textBlock;

        // Hover effect
        if (hoverBrush is not null)
        {
            border.MouseEnter += (_, _) => border.Background = hoverBrush;
            border.MouseLeave += (_, _) => border.Background = backBrush;
        }

        return border;
    }

    /// <summary>
    /// Creates a category header label with a top separator line,
    /// visually separating sections in the dropdown.
    /// </summary>
    private static Border CreateCategoryHeader(
        string categoryText,
        System.Windows.Media.Brush foreBrush,
        System.Windows.Media.Brush backBrush,
        System.Windows.Media.Brush borderBrush)
    {
        Border container = new()
        {
            Padding = new Thickness(0),
            Background = backBrush,
        };

        StackPanel stack = new();
        container.Child = stack;

        // Thin separator line above the header
        stack.Children.Add(new Border
        {
            Height = 1,
            Background = borderBrush,
            Opacity = 0.3,
            Margin = new Thickness(0, 4, 0, 0)
        });

        // Header text
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

    /// <summary>
    /// Populates the preset editor fields (system prompt + tool checkboxes)
    /// from the given mode's settings, and creates a new preset with those values.
    /// </summary>
    private void CopyFromMode(IAiChatMode sourceMode)
    {
        string baseName = sourceMode.DisplayName;
        // Deduplicate: if "Agent (Copy)" already exists, still just use "Agent (Copy)"
        string newName = $"{baseName} (Copy)";

        // Get system prompt
        string? systemPrompt = sourceMode.BuildSystemPrompt(default);

        // Get allowed tools
        HashSet<string>? allowedTools = sourceMode.AllowedTools is not null
            ? new HashSet<string>(sourceMode.AllowedTools)
            : null;

        AiPreset newPreset = new()
        {
            Name = newName,
            SystemPrompt = systemPrompt,
            AllowedTools = allowedTools
        };

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

        string message = $"Delete preset \"{_currentPreset.Name}\"?";
        MessageBoxResult result = MessageBox.Show(this, message, "Delete Preset", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _presets.Remove(_currentPreset);
        _currentPreset = null;

        if (_presets.Count > 0)
        {
            PresetSelector.SelectedIndex = 0;
            SelectPreset(_presets[0]);
        }
        else
        {
            SelectPreset(null);
        }

        RefreshPresetSelector();
        MarkDirty();
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

        _currentPreset.SystemPrompt = SystemPromptBox.Text;
        SaveButton.IsEnabled = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreset is null)
        {
            return;
        }

        // Sync name and prompt from UI
        _currentPreset.Name = PresetNameBox.Text;
        _currentPreset.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        _currentPreset.AllowedTools = GetSelectedTools();

        AiPresetManager.Save(_presets);
        SaveButton.IsEnabled = false;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
