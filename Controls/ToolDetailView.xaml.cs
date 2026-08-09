using KaneCode.Models;
using KaneCode.Services.Ai;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace KaneCode.Controls;

/// <summary>
/// Right-hand detail pane of the preset editor. Binds to a single
/// <see cref="ToolEditState"/> and exposes:
/// a compact tool header, a description editor with highlighted
/// <c>{param}</c> tokens, and a tabbed area (Parameters / Backend Options /
/// Tool definition) whose content is generated from the tool's JSON schemas.
/// </summary>
public partial class ToolDetailView : UserControl
{
    private sealed class SchemaWidget
    {
        public required FrameworkElement Element { get; init; }

        public required Func<JsonElement> ReadValue { get; init; }

        public required Action<JsonElement> WriteValue { get; init; }

        public Action? RefreshVisuals { get; set; }
    }

    private AgentToolRegistry? _registry;
    private ToolEditState? _state;
    private AiPreset? _preset;
    private Action? _onChanged;
    private bool _loading = true;
    private string _cleanDefinitionJson = string.Empty;
    private bool _reflowPending;
    private bool _descriptionHighlightDirty;

    public ToolDetailView()
    {
        InitializeComponent();
    }

    private ToolEditState State => _state ?? throw new InvalidOperationException("ToolDetailView is not initialized.");

    private AgentToolRegistry Registry => _registry ?? throw new InvalidOperationException("ToolDetailView is not initialized.");

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

    // ── Initialization ─────────────────────────────────────────────

    internal void Initialize(AgentToolRegistry registry, ToolEditState state, AiPreset preset, Action onChanged)
    {
        _registry = registry;
        _state = state;
        _preset = preset;
        _onChanged = onChanged;
        LoadTool();
    }

    private void LoadTool()
    {
        _loading = true;
        try
        {
            ToolNameText.Text = State.Tool.Name;
            ToolCategoryText.Text = State.Tool.Category?.ToUpperInvariant() ?? "GENERAL";
            ToolIconText.Text = GetToolGlyph(State.Tool.Category);

            IReadOnlyList<string> required = AgentToolRegistry.GetRequiredParameters(State.Tool);
            IReadOnlyList<string> all = AgentToolRegistry.GetParameterNames(State.Tool);
            ToolParamsText.Text = required.Count > 0
                ? $"{required.Count} required param{(required.Count == 1 ? "" : "s")}"
                : $"{all.Count} param{(all.Count == 1 ? "" : "s")}";
            ToolSummaryText.Text = State.Tool.Description;

            EnabledToggle.IsChecked = State.IsEnabled;

            // Description
            string initialDescription = State.DescriptionOverride ?? State.Tool.Description;
            DescriptionEditor.Document = BuildHighlightedDocument(initialDescription);
            DescriptionEditor.CaretPosition = DescriptionEditor.Document.ContentEnd;
            UpdateDescriptionVisuals(initialDescription);

            // Param chips
            ParamChipsPanel.Items.Clear();
            foreach (string paramName in all)
            {
                Button chip = new()
                {
                    Content = "{" + paramName + "}",
                    Tag = paramName,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                    FontSize = 9.5,
                    Foreground = Brush("DiagnosticInfoForeground"),
                    Background = Brush("ControlBackground"),
                    BorderBrush = Brush("ControlBorder"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = $"Insert {{{paramName}}} at the caret"
                };
                chip.Click += ParamChip_Click;
                ParamChipsPanel.Items.Add(chip);
            }

            RebuildParametersPanel();
            RebuildBackendOptionsPanel();
            RebuildDefinition();

            // Parameters is the default tab so the parameter overrides (pin / hide)
            // are the first thing the user sees for every tool.
            ShowTab("parameters");
        }
        finally
        {
            _loading = false;
        }
    }

    private static string GetToolGlyph(string? category)
    {
        return category?.ToUpperInvariant() switch
        {
            "READ FILES" => "📖",
            "WRITE FILES" => "✏️",
            "GIT" => "⎇",
            "DOTNET" or "BUILD & TEST" => "⚒",
            "DRAWING" => "🎨",
            "NUGET" => "📦",
            "MULTI-AGENT" => "🕸",
            "APPLICATION" => "🖥",
            _ => "⚙"
        };
    }

    private void MarkChanged()
    {
        if (!_loading)
        {
            _onChanged?.Invoke();
        }
    }

    // ── Description editor ─────────────────────────────────────────

    private void DescriptionEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || State is null)
        {
            return;
        }

        string text = GetDescriptionText();
        State.DescriptionOverride = text;

        // The {param} highlighting is applied by replacing the whole FlowDocument,
        // which resets the caret. Never do that while the user is actively typing —
        // defer it until the editor loses focus (or the tool is reloaded) so the
        // caret always stays exactly where the user put it.
        if (DescriptionEditor.IsKeyboardFocusWithin)
        {
            _descriptionHighlightDirty = true;
        }
        else
        {
            QueueDescriptionReflow();
        }

        UpdateDescriptionVisuals(text);
        MarkChanged();
    }

    private void DescriptionEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || !_descriptionHighlightDirty)
        {
            return;
        }

        // Keep the reflow deferred by one dispatcher turn: if focus moved straight
        // back into the editor (e.g. clicking a param chip), the highlight still
        // does not need to be rebuilt.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_descriptionHighlightDirty && !DescriptionEditor.IsKeyboardFocusWithin)
            {
                QueueDescriptionReflow();
            }
        }));
    }

    private void QueueDescriptionReflow()
    {
        if (_reflowPending)
        {
            return;
        }

        _reflowPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ReflowDescription));
    }

    private void ReflowDescription()
    {
        _reflowPending = false;
        _descriptionHighlightDirty = false;
        if (_loading || State is null)
        {
            return;
        }

        string text = GetDescriptionText();
        _loading = true;
        try
        {
            // The caret is restored after the rebuild by plain-text offset. This
            // runs only while the editor is unfocused, so a slightly off position
            // is harmless — it can never move the caret out from under the user.
            int caretOffset = new TextRange(DescriptionEditor.Document.ContentStart, DescriptionEditor.CaretPosition).Text.Length;
            DescriptionEditor.Document = BuildHighlightedDocument(text);
            RestoreCaret(caretOffset);
        }
        finally
        {
            _loading = false;
        }

        // The description is part of the model-facing tool definition, so keep the
        // preview in sync once the user is done editing (reflow runs on blur).
        RebuildDefinition();
    }

    private void UpdateDescriptionVisuals(string text)
    {
        bool modified = State is not null && State.DescriptionModified;
        DescriptionModifiedBar.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
        DescriptionEditorFrame.BorderBrush = modified ? Brush("Brush.AmberSoftBorder") : Brush("Brush.ControlBorder");

        int dynamicRefs = CountDynamicRefs(text);
        DescriptionMetaText.Text = $"{text.Length} chars · {EstimateTokens(text)} tokens · {dynamicRefs} dynamic refs";

        DescriptionSubLabel.Text = modified
            ? "Sent to the model in every tool definition · modified vs tool default"
            : "Sent to the model in every tool definition";

        UseDefaultDescriptionButton.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
    }

    private string GetDescriptionText()
    {
        string text = new TextRange(DescriptionEditor.Document.ContentStart, DescriptionEditor.Document.ContentEnd).Text;
        text = text.TrimEnd('\r', '\n');
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private FlowDocument BuildHighlightedDocument(string text)
    {
        FlowDocument doc = new() { PagePadding = new Thickness(0) };
        IReadOnlyList<string> paramNames = _state is null ? [] : AgentToolRegistry.GetParameterNames(_state.Tool);
        HashSet<string> known = new(paramNames, StringComparer.Ordinal);

        string[] lines = text.Split('\n');
        foreach (string line in lines)
        {
            Paragraph paragraph = new()
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                LineHeight = 18
            };
            AppendHighlightedRuns(paragraph, line, known);
            doc.Blocks.Add(paragraph);
        }

        return doc;
    }

    private void AppendHighlightedRuns(Paragraph paragraph, string line, HashSet<string> knownParams)
    {
        int index = 0;
        while (index < line.Length)
        {
            int open = line.IndexOf('{', index);
            if (open < 0)
            {
                AddRun(paragraph, line[index..], isToken: false, isOrphan: false);
                break;
            }

            if (open > index)
            {
                AddRun(paragraph, line[index..open], isToken: false, isOrphan: false);
            }

            int close = line.IndexOf('}', open + 1);
            if (close < 0)
            {
                AddRun(paragraph, line[open..], isToken: false, isOrphan: false);
                break;
            }

            string token = line.Substring(open + 1, close - open - 1);
            AddRun(paragraph, "{" + token + "}", isToken: true, isOrphan: !knownParams.Contains(token));
            index = close + 1;
        }
    }

    private void AddRun(Paragraph paragraph, string text, bool isToken, bool isOrphan)
    {
        if (text.Length == 0)
        {
            return;
        }

        Run run = new(text)
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            Foreground = isToken
                ? (isOrphan ? Brush("Brush.Red") : Brush("Brush.Amber"))
                : Brush("Brush.Text")
        };

        if (isToken)
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        paragraph.Inlines.Add(run);
    }

    private void RestoreCaret(int caretOffset)
    {
        int totalLength = new TextRange(DescriptionEditor.Document.ContentStart, DescriptionEditor.Document.ContentEnd).Text.Length;
        TextPointer? target = DescriptionEditor.Document.ContentStart.GetPositionAtOffset(
            Math.Min(caretOffset, totalLength),
            LogicalDirection.Forward);
        if (target is not null)
        {
            DescriptionEditor.CaretPosition = target;
        }
    }

    private static int CountDynamicRefs(string text)
    {
        return Regex.Matches(text, @"\{[^{}\s]+\}").Count;
    }

    private static int EstimateTokens(string text)
    {
        // Rough heuristic: ~4 characters per token.
        return Math.Max(0, (int)Math.Ceiling(text.Length / 4.0));
    }

    private void UseDefaultDescription_Click(object sender, RoutedEventArgs e)
    {
        if (State is null)
        {
            return;
        }

        State.DescriptionOverride = null;
        _loading = true;
        try
        {
            DescriptionEditor.Document = BuildHighlightedDocument(State.Tool.Description);
            DescriptionEditor.CaretPosition = DescriptionEditor.Document.ContentEnd;
            UpdateDescriptionVisuals(State.Tool.Description);
        }
        finally
        {
            _loading = false;
        }

        RebuildDefinition();
        MarkChanged();
    }

    private void InsertParamAtCaret(string paramName)
    {
        string token = "{" + paramName + "}";
        TextRange caretRange = new(DescriptionEditor.CaretPosition, DescriptionEditor.CaretPosition);
        caretRange.Text = token;
        DescriptionEditor.Focus();
    }

    private void ParamChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string paramName)
        {
            InsertParamAtCaret(paramName);
        }
    }

    // ── Tabs ───────────────────────────────────────────────────────

    private void ShowTab(string tab)
    {
        ParametersPanel.Visibility = tab == "parameters" ? Visibility.Visible : Visibility.Collapsed;
        BackendOptionsPanel.Visibility = tab == "backend" ? Visibility.Visible : Visibility.Collapsed;
        DefinitionPanel.Visibility = tab == "definition" ? Visibility.Visible : Visibility.Collapsed;

        SetTabActive(ParametersTabButton, tab == "parameters");
        SetTabActive(BackendTabButton, tab == "backend");
        SetTabActive(DefinitionTabButton, tab == "definition");
    }

    private void SetTabActive(Button button, bool active)
    {
        button.Foreground = active ? Brush("WindowForeground") : Brush("PanelHeaderForeground");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ParametersTab_Click(object sender, RoutedEventArgs e) => ShowTab("parameters");

    private void BackendTab_Click(object sender, RoutedEventArgs e) => ShowTab("backend");

    private void DefinitionTab_Click(object sender, RoutedEventArgs e) => ShowTab("definition");

    // ── Enabled toggle ─────────────────────────────────────────────

    private void EnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || State is null)
        {
            return;
        }

        State.IsEnabled = EnabledToggle.IsChecked == true;
        MarkChanged();
    }

    // ── Parameters tab ─────────────────────────────────────────────

    private void RebuildParametersPanel()
    {
        ParametersPanel.Children.Clear();

        IReadOnlyList<string> paramNames = AgentToolRegistry.GetParameterNames(State.Tool);
        IReadOnlySet<string> required = new HashSet<string>(AgentToolRegistry.GetRequiredParameters(State.Tool), StringComparer.Ordinal);
        JsonElement properties = State.Tool.ParametersSchema.ValueKind == JsonValueKind.Object &&
                                 State.Tool.ParametersSchema.TryGetProperty("properties", out JsonElement p)
            ? p
            : default;

        int pinnedCount = State.PinnedParameters.Count;
        int hiddenCount = State.HiddenParameters.Count;
        ParametersHeaderCount.Text = $"{paramNames.Count} shown · {pinnedCount} pinned · {hiddenCount} hidden";

        if (paramNames.Count == 0)
        {
            ParametersPanel.Children.Add(BuildEmptyState("This tool has no parameters."));
            return;
        }

        foreach (string name in paramNames)
        {
            JsonElement prop = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out JsonElement value)
                ? value
                : default;
            ParametersPanel.Children.Add(BuildParameterRow(name, prop, required.Contains(name)));
        }
    }

    private Border BuildParameterRow(string name, JsonElement prop, bool isRequired)
    {
        bool isPinned = State.PinnedParameters.ContainsKey(name);
        bool isHidden = State.HiddenParameters.Contains(name);
        JsonElement defaultValue = GetSchemaDefault(prop, type: GetPropertyString(prop, "type") ?? "string");
        string typeName = GetPropertyString(prop, "type") ?? "string";
        string? description = GetPropertyString(prop, "description");
        bool isEnum = prop.ValueKind == JsonValueKind.Object &&
                      prop.TryGetProperty("enum", out JsonElement enumArray) &&
                      enumArray.ValueKind == JsonValueKind.Array &&
                      enumArray.GetArrayLength() > 0;

        SchemaWidget widget = BuildWidget(
            name,
            prop,
            typeName,
            isEnum,
            isPinned ? State.PinnedParameters[name] : defaultValue);

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // amber bar
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // hide
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // pin

        Border amberBar = new()
        {
            Width = 3,
            Background = Brush("Brush.Amber"),
            Margin = new Thickness(0, 6, 0, 6),
            Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed
        };
        grid.Children.Add(amberBar);

        StackPanel info = new() { Margin = new Thickness(10, 6, 8, 6) };
        StackPanel topRow = new() { Orientation = Orientation.Horizontal };
        TextBlock nameText = new()
        {
            Text = name,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Brush.Text")
        };
        topRow.Children.Add(nameText);

        topRow.Children.Add(BuildTypeChip(typeName));
        if (isRequired)
        {
            topRow.Children.Add(BuildTextChip("required", Brush("Brush.Red")));
        }
        else
        {
            topRow.Children.Add(BuildTextChip("optional", Brush("Brush.Faint")));
        }

        Border pinnedPill = BuildTextChip("Pinned", Brush("Brush.Amber"));
        pinnedPill.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
        topRow.Children.Add(pinnedPill);

        Border hiddenPill = BuildTextChip("Hidden", Brush("Brush.Faint"));
        hiddenPill.Visibility = isHidden ? Visibility.Visible : Visibility.Collapsed;
        topRow.Children.Add(hiddenPill);
        info.Children.Add(topRow);

        TextBlock descriptionText = new()
        {
            Text = description,
            FontSize = 10.5,
            Foreground = Brush("Brush.Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 4)
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            info.Children.Add(descriptionText);
        }

        widget.Element.Margin = new Thickness(0, 0, 0, 2);
        widget.Element.IsEnabled = isPinned && !isHidden;
        info.Children.Add(widget.Element);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        // Pin button (available for every parameter; disabled while hidden).
        ToggleButton pinButton = new()
        {
            Content = isPinned ? "🔒" : "🔓",
            IsChecked = isPinned,
            IsEnabled = !isHidden,
            ToolTip = isPinned ? "Unpin (restore default)" : "Pin a value for the agent",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Background = isPinned ? Brush("Brush.AmberSoftBg") : Brush("Brush.PanelBg"),
            BorderBrush = isPinned ? Brush("Brush.AmberSoftBorder") : Brush("Brush.ControlBorder"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        Grid.SetColumn(pinButton, 3);
        grid.Children.Add(pinButton);

        // Hide/disable toggle — only for non-required parameters.
        ToggleButton? hideButton = null;
        if (!isRequired)
        {
            hideButton = new ToggleButton
            {
                Content = isHidden ? "Show" : "Hide",
                IsChecked = isHidden,
                ToolTip = isHidden
                    ? "Show this parameter to the agent"
                    : "Hide this parameter from the agent",
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 4, 0),
                Background = isHidden ? Brush("Brush.AmberSoftBg") : Brush("Brush.PanelBg"),
                BorderBrush = isHidden ? Brush("Brush.AmberSoftBorder") : Brush("Brush.ControlBorder"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 3, 6, 3),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Grid.SetColumn(hideButton, 2);
            grid.Children.Add(hideButton);
        }

        // Applies the hidden visual state to the whole row. Hidden rows are greyed
        // out (name/description muted, reduced opacity), the value widget is
        // disabled, the pin button is disabled, and a "Hidden" pill is shown.
        void ApplyRowState(bool hidden)
        {
            bool pinned = State.PinnedParameters.ContainsKey(name);
            nameText.Foreground = hidden ? Brush("Brush.Faint") : Brush("Brush.Text");
            descriptionText.Foreground = hidden ? Brush("Brush.Faint") : Brush("Brush.Muted");
            info.Opacity = hidden ? 0.55 : 1.0;
            RefreshPinVisuals(amberBar, pinnedPill, pinButton, isPinned: pinned && !hidden);
            widget.Element.IsEnabled = !hidden && pinned;
            hiddenPill.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
            pinButton.IsEnabled = !hidden;
            pinButton.IsChecked = pinned && !hidden;
            if (hideButton is not null)
            {
                hideButton.Content = hidden ? "Show" : "Hide";
                hideButton.ToolTip = hidden
                    ? "Show this parameter to the agent"
                    : "Hide this parameter from the agent";
            }
        }

        pinButton.Checked += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            State.SetPinnedParameter(name, widget.ReadValue());
            RefreshPinVisuals(amberBar, pinnedPill, pinButton, isPinned: true);
            widget.Element.IsEnabled = !State.HiddenParameters.Contains(name);
            RebuildDefinition();
            MarkChanged();
        };
        pinButton.Unchecked += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            State.RemovePinnedParameter(name);
            widget.WriteValue(defaultValue);
            RefreshPinVisuals(amberBar, pinnedPill, pinButton, isPinned: false);
            widget.Element.IsEnabled = false;
            RebuildDefinition();
            MarkChanged();
        };

        if (hideButton is not null)
        {
            hideButton.Checked += (_, _) =>
            {
                if (_loading)
                {
                    return;
                }

                State.HideParameter(name);
                State.RemovePinnedParameter(name); // hidden and pinned are mutually exclusive
                widget.WriteValue(defaultValue);
                ApplyRowState(hidden: true);
                RebuildDefinition();
                MarkChanged();
            };
            hideButton.Unchecked += (_, _) =>
            {
                if (_loading)
                {
                    return;
                }

                State.UnhideParameter(name);
                ApplyRowState(hidden: false);
                RebuildDefinition();
                MarkChanged();
            };
        }

        ApplyRowState(isHidden);

        return new Border
        {
            Background = Brush("PanelBackground"),
            BorderBrush = Brush("ControlBorder"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 4),
            Child = grid
        };
    }

    private void RefreshPinVisuals(Border amberBar, Border pinnedPill, ToggleButton pinButton, bool isPinned)
    {
        amberBar.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
        pinnedPill.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
        pinButton.Content = isPinned ? "🔒" : "🔓";
        pinButton.ToolTip = isPinned ? "Unpin (restore default)" : "Pin a value for the agent";
        pinButton.Background = isPinned ? Brush("AiChatToolCallBackground") : Brush("Button.Static.Background");
        pinButton.BorderBrush = isPinned ? Brush("AiChatToolCallBorder") : Brush("Button.Static.Border");
    }

    // ── Backend Options tab ────────────────────────────────────────

    private void RebuildBackendOptionsPanel()
    {
        BackendOptionsPanel.Children.Clear();

        JsonElement schema = State.Tool.BackendOptionsSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out JsonElement props) ||
            props.ValueKind != JsonValueKind.Object)
        {
            BackendOptionsPanel.Children.Add(BuildEmptyState("This tool has no configurable backend options."));
            return;
        }

        IReadOnlyDictionary<string, JsonElement> defaults = State.Tool.DefaultBackendOptions;
        string currentEngine = GetEffectiveOptionString("engine", defaults);

        int customized = CountCustomizedOptions(props, defaults);
        BackendOptionsPanel.Children.Add(BuildBackendSummaryBar(customized));

        // Implementation card (engine enum)
        if (props.TryGetProperty("engine", out JsonElement engineProp) && IsEnumProperty(engineProp))
        {
            BackendOptionsPanel.Children.Add(BuildImplementationCard(engineProp, currentEngine));
        }

        // Engine-scoped options (e.g. matching behavior)
        List<JsonProperty> scoped = props.EnumerateObject()
            .Where(p => p.Name != "engine" && HasEngine(p.Value, currentEngine))
            .ToList();
        if (scoped.Count > 0)
        {
            BackendOptionsPanel.Children.Add(BuildOptionCard("Matching", scoped, defaults, $"applies to: {FormatOptionName(currentEngine)}"));
        }

        // Execution & safety options (not engine-scoped)
        List<JsonProperty> execution = props.EnumerateObject()
            .Where(p => p.Name != "engine" && !HasEnginesAnnotation(p.Value))
            .ToList();
        if (execution.Count > 0)
        {
            BackendOptionsPanel.Children.Add(BuildOptionCard("Execution & Safety", execution, defaults, "defaults inherited from tool"));
        }
    }

    private Border BuildBackendSummaryBar(int customized)
    {
        Border bar = new()
        {
            Background = Brush("AiChatToolCallBackground"),
            BorderBrush = Brush("AiChatToolCallBorder"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 8)
        };

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock text = new()
        {
            Text = customized == 0
                ? "No backend options customized for this agent — these control execution, not the model-facing schema"
                : $"{customized} backend option{(customized == 1 ? "" : "s")} customized for this agent — these control execution, not the model-facing schema",
            FontSize = 10.5,
            Foreground = Brush("AiChatToolCallForeground"),
            TextWrapping = TextWrapping.Wrap
        };
        grid.Children.Add(text);

        grid.Children.Add(new TextBlock
        {
            Text = "Per-preset, not sent to the model",
            FontSize = 10.5,
            Foreground = Brush("DiagnosticInfoForeground"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        });

        bar.Child = grid;
        return bar;
    }

    private Border BuildImplementationCard(JsonElement engineProp, string currentEngine)
    {
        IReadOnlyList<string> engines = GetEnumValues(engineProp);
        Dictionary<string, string> descriptions = GetEnumDescriptions(engineProp);
        HashSet<string> recommended = GetRecommendedEngines(engineProp);

        StackPanel panel = new();
        Border card = BuildCard("IMPLEMENTATION", "Which engine executes this tool for this preset", $"{engines.Count} engine{(engines.Count == 1 ? "" : "s")}", panel);

        foreach (string engine in engines)
        {
            bool isSelected = string.Equals(engine, currentEngine, StringComparison.Ordinal);
            Grid row = new() { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            RadioButton radio = new()
            {
                GroupName = "engine_" + State.Tool.Name,
                IsChecked = isSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 0),
                Foreground = Brush("Brush.Text"),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Grid.SetColumn(radio, 0);
            row.Children.Add(radio);

            StackPanel info = new();
            StackPanel nameRow = new() { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock
            {
                Text = FormatOptionName(engine),
                FontSize = 11.5,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isSelected ? Brush("WindowForeground") : Brush("Brush.Text")
            });
            if (recommended.Contains(engine))
            {
                nameRow.Children.Add(BuildTextChip("recommended", Brush("Brush.Green")));
            }

            info.Children.Add(nameRow);
            info.Children.Add(new TextBlock
            {
                Text = descriptions.TryGetValue(engine, out string? desc) ? desc : string.Empty,
                FontSize = 10,
                Foreground = Brush("Brush.Muted")
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            radio.Checked += (_, _) =>
            {
                if (_loading || isSelected)
                {
                    return;
                }

                State.SetOptionOverride("engine", JsonSerializer.SerializeToElement(engine));
                MarkChanged();
                _loading = true;
                try
                {
                    RebuildBackendOptionsPanel();
                }
                finally
                {
                    _loading = false;
                }
            };

            panel.Children.Add(row);
        }

        return card;
    }

    private Border BuildOptionCard(string title, IReadOnlyList<JsonProperty> options, IReadOnlyDictionary<string, JsonElement> defaults, string rightLabel)
    {
        StackPanel rows = new();
        Border card = BuildCard(title, null, rightLabel, rows);

        foreach (JsonProperty option in options)
        {
            string name = option.Name;
            JsonElement prop = option.Value;
            bool customized = IsOptionCustomized(name, prop, defaults);
            JsonElement defaultValue = GetSchemaDefault(prop, GetPropertyString(prop, "type") ?? "string");
            string typeName = GetPropertyString(prop, "type") ?? "string";
            string? description = GetPropertyString(prop, "description");
            bool isEnum = IsEnumProperty(prop);

            JsonElement effectiveValue = GetEffectiveOption(name, defaults);

            Grid grid = new() { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border amberBar = new()
            {
                Width = 3,
                Background = Brush("Brush.Amber"),
                Margin = new Thickness(0, 6, 0, 6),
                Visibility = customized ? Visibility.Visible : Visibility.Collapsed
            };
            grid.Children.Add(amberBar);

            SchemaWidget widget = BuildWidget(name, prop, typeName, isEnum, effectiveValue, refreshVisuals: () =>
            {
                amberBar.Visibility = IsOptionCustomized(name, prop, defaults) ? Visibility.Visible : Visibility.Collapsed;
            });

            StackPanel info = new() { Margin = new Thickness(10, 4, 8, 4) };
            StackPanel labelRow = new() { Orientation = Orientation.Horizontal };
            labelRow.Children.Add(new TextBlock
            {
                Text = FormatOptionName(name),
                FontSize = 11.5,
                Foreground = customized ? Brush("Brush.Amber") : Brush("Brush.Text"),
                FontWeight = customized ? FontWeights.SemiBold : FontWeights.Normal
            });
            if (!string.IsNullOrWhiteSpace(description))
            {
                labelRow.Children.Add(new TextBlock
                {
                    Text = "  —  " + description,
                    FontSize = 10,
                    Foreground = Brush("Brush.Muted"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            info.Children.Add(labelRow);
            info.Children.Add(widget.Element);
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            if (customized)
            {
                Button restore = new()
                {
                    Content = "↺",
                    ToolTip = "Restore tool default",
                    Width = 22,
                    Height = 22,
                    FontSize = 12,
                    Foreground = Brush("Brush.Amber"),
                    Background = Brush("Brush.PanelBg"),
                    BorderBrush = Brush("Brush.ControlBorder"),
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 6, 6, 0)
                };
                restore.Click += (_, _) =>
                {
                    State.RemoveOptionOverride(name);
                    RebuildBackendOptionsPanel();
                    MarkChanged();
                };
                Grid.SetColumn(restore, 2);
                grid.Children.Add(restore);
            }

            widget.Element.Tag = name;

            rows.Children.Add(new Border
            {
                Background = Brush("PanelBackground"),
                BorderBrush = Brush("ControlBorder"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 4),
                Child = grid
            });
        }

        return card;
    }

    private Border BuildCard(string title, string? subLabel, string rightLabel, StackPanel content)
    {
        Border card = new()
        {
            Background = Brush("Brush.PanelBg"),
            BorderBrush = Brush("Brush.Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        StackPanel stack = new();
        Grid header = new();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel titles = new();
        titles.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Brush.Text")
        });
        if (!string.IsNullOrWhiteSpace(subLabel))
        {
            titles.Children.Add(new TextBlock
            {
                Text = subLabel,
                FontSize = 10,
                Foreground = Brush("Brush.Muted")
            });
        }

        header.Children.Add(titles);
        header.Children.Add(new TextBlock
        {
            Text = rightLabel,
            FontSize = 10,
            Foreground = Brush("Brush.Faint"),
            VerticalAlignment = VerticalAlignment.Top
        });

        stack.Children.Add(header);
        stack.Children.Add(content);
        card.Child = stack;
        return card;
    }

    private Border BuildEmptyState(string message)
    {
        return new Border
        {
            Background = Brush("Brush.PanelBg"),
            BorderBrush = Brush("Brush.Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 24, 16, 24),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = message,
                FontSize = 11.5,
                Foreground = Brush("Brush.Muted"),
                TextAlignment = TextAlignment.Center
            }
        };
    }

    // ── Widgets (shared schema → control mapper) ───────────────────

    private SchemaWidget BuildWidget(string name, JsonElement prop, string typeName, bool isEnum, JsonElement value, Action? refreshVisuals = null)
    {
        SchemaWidget widget;
        if (isEnum)
        {
            widget = BuildComboWidget(name, prop, value, refreshVisuals);
        }
        else
        {
            widget = typeName switch
            {
                "boolean" => BuildBooleanWidget(name, value, refreshVisuals),
                "integer" or "number" => BuildNumberWidget(name, prop, value, isInteger: typeName == "integer", refreshVisuals),
                _ => BuildTextWidget(name, prop, value, isMultiline: IsMultilineParameter(name, prop), refreshVisuals)
            };
        }

        widget.RefreshVisuals = refreshVisuals;
        return widget;
    }

    private SchemaWidget BuildTextWidget(string name, JsonElement prop, JsonElement value, bool isMultiline, Action? refreshVisuals = null)
    {
        TextBox box = new()
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 11.5,
            Foreground = Brush("Brush.Text"),
            Background = Brush("Brush.InsetBg"),
            BorderBrush = Brush("Brush.ControlBorder"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 2, 5, 2),
            AcceptsReturn = isMultiline,
            TextWrapping = isMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = isMultiline ? 54 : 22,
            VerticalScrollBarVisibility = isMultiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            box.Text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        }

        box.TextChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            bool isPinnedParam = State.PinnedParameters.ContainsKey(name);
            bool isOption = State.OptionOverrides.ContainsKey(name) || State.Tool.DefaultBackendOptions.ContainsKey(name);
            if (!isPinnedParam && !isOption)
            {
                return;
            }

            SetWidgetValue(name, JsonSerializer.SerializeToElement(box.Text));
            refreshVisuals?.Invoke();
        };

        return new SchemaWidget
        {
            Element = box,
            ReadValue = () => JsonSerializer.SerializeToElement(box.Text),
            WriteValue = v => box.Text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText()
        };
    }

    private SchemaWidget BuildBooleanWidget(string name, JsonElement value, Action? refreshVisuals = null)
    {
        CheckBox toggle = new()
        {
            IsChecked = value.ValueKind == JsonValueKind.True,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4)
        };
        toggle.Checked += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            SetWidgetValue(name, JsonSerializer.SerializeToElement(true));
            refreshVisuals?.Invoke();
        };
        toggle.Unchecked += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            SetWidgetValue(name, JsonSerializer.SerializeToElement(false));
            refreshVisuals?.Invoke();
        };

        return new SchemaWidget
        {
            Element = toggle,
            ReadValue = () => JsonSerializer.SerializeToElement(toggle.IsChecked == true),
            WriteValue = v => toggle.IsChecked = v.ValueKind == JsonValueKind.True
        };
    }

    private SchemaWidget BuildComboWidget(string name, JsonElement prop, JsonElement value, Action? refreshVisuals = null)
    {
        IReadOnlyList<string> values = GetEnumValues(prop);
        ComboBox combo = new()
        {
            ItemsSource = values,
            SelectedItem = value.ValueKind == JsonValueKind.String ? value.GetString() : null,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 11.5,
            Foreground = Brush("Brush.Text"),
            Background = Brush("Brush.InsetBg"),
            BorderBrush = Brush("Brush.ControlBorder"),
            BorderThickness = new Thickness(1),
            MinHeight = 22,
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 140
        };
        if (combo.SelectedItem is null && values.Count > 0)
        {
            combo.SelectedItem = values[0];
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            if (combo.SelectedItem is string selected)
            {
                SetWidgetValue(name, JsonSerializer.SerializeToElement(selected));
                refreshVisuals?.Invoke();
            }
        };

        return new SchemaWidget
        {
            Element = combo,
            ReadValue = () => JsonSerializer.SerializeToElement(combo.SelectedItem as string ?? string.Empty),
            WriteValue = v => combo.SelectedItem = v.ValueKind == JsonValueKind.String ? v.GetString() : null
        };
    }

    private SchemaWidget BuildNumberWidget(string name, JsonElement prop, JsonElement value, bool isInteger, Action? refreshVisuals = null)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBox box = new()
        {
            Text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : string.Empty,
            Width = 56,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 11.5,
            Foreground = Brush("Brush.Text"),
            Background = Brush("Brush.InsetBg"),
            BorderBrush = Brush("Brush.ControlBorder"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 1, 4, 1),
            MinHeight = 22,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.TextChanged += (_, _) =>
        {
            SetWidgetValue(name, ParseNumber(box.Text, isInteger));
            refreshVisuals?.Invoke();
        };

        Button minus = CreateStepperButton("−");
        Button plus = CreateStepperButton("+");
        Grid.SetColumn(minus, 1);
        Grid.SetColumn(plus, 2);
        minus.Margin = new Thickness(2, 0, 2, 0);
        plus.Margin = new Thickness(0, 0, 0, 0);

        minus.Click += (_, _) => StepNumber(box, -1, isInteger);
        plus.Click += (_, _) => StepNumber(box, +1, isInteger);

        grid.Children.Add(box);
        grid.Children.Add(minus);
        grid.Children.Add(plus);

        return new SchemaWidget
        {
            Element = grid,
            ReadValue = () => ParseNumber(box.Text, isInteger),
            WriteValue = v => box.Text = v.ValueKind == JsonValueKind.Number ? v.GetRawText() : string.Empty
        };
    }

    private void StepNumber(TextBox box, int delta, bool isInteger)
    {
        if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double current))
        {
            box.Text = isInteger
                ? ((int)Math.Round(current) + delta).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : (current + delta).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (isInteger)
        {
            box.Text = delta.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static JsonElement ParseNumber(string text, bool isInteger)
    {
        if (isInteger && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int intValue))
        {
            return JsonSerializer.SerializeToElement(intValue);
        }

        if (!isInteger && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double doubleValue))
        {
            return JsonSerializer.SerializeToElement(doubleValue);
        }

        return isInteger ? JsonSerializer.SerializeToElement(0) : JsonSerializer.SerializeToElement(0.0);
    }

    private Button CreateStepperButton(string content)
    {
        return new Button
        {
            Content = content,
            Width = 22,
            Height = 22,
            FontSize = 13,
            Foreground = Brush("Brush.Text"),
            Background = Brush("Brush.PanelBg"),
            BorderBrush = Brush("Brush.ControlBorder"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(0)
        };
    }

    private void SetWidgetValue(string name, JsonElement value)
    {
        if (State is null)
        {
            return;
        }

        if (State.OptionOverrides.ContainsKey(name) || State.Tool.DefaultBackendOptions.ContainsKey(name))
        {
            State.SetOptionOverride(name, value);
            MarkChanged();
        }
        else if (State.PinnedParameters.ContainsKey(name))
        {
            State.SetPinnedParameter(name, value);
            MarkChanged();
        }
    }

    private bool IsMultilineParameter(string name, JsonElement prop)
    {
        if (name is "content" or "prompt" or "systemPrompt" or "newText" or "oldText" or "message" or "description")
        {
            return true;
        }

        string? description = GetPropertyString(prop, "description");
        return description is { Length: > 70 };
    }

    private Border BuildTypeChip(string typeName)
    {
        return BuildTextChip(typeName, Brush("Brush.Faint"));
    }

    private Border BuildTextChip(string text, Brush foreground)
    {
        Border chip = new()
        {
            Background = Brush("Brush.PanelBg"),
            BorderBrush = Brush("Brush.ControlBorder"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(8, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        chip.Child = new TextBlock
        {
            Text = text,
            FontSize = 9,
            Foreground = foreground
        };
        return chip;
    }

    // ── Tool definition tab ────────────────────────────────────────

    private void RebuildDefinition()
    {
        if (Registry is null || State is null || _preset is null)
        {
            return;
        }

        AiPreset working = BuildWorkingPreset();
        JsonObject definition = Registry.BuildToolDefinition(State.Tool, working);
        _cleanDefinitionJson = definition.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        DefinitionText.Text = AddHiddenMarker(
            AddPinnedMarkers(_cleanDefinitionJson, State.PinnedParameters.Keys),
            State.HiddenParameters);
    }

    private AiPreset BuildWorkingPreset()
    {
        AiPreset working = new() { Name = _preset!.Name };

        if (State.DescriptionModified)
        {
            working.ToolDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [State.Tool.Name] = State.DescriptionOverride!
            };
        }

        if (State.PinnedParameters.Count > 0)
        {
            working.PinnedParameters = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                [State.Tool.Name] = new Dictionary<string, JsonElement>(State.PinnedParameters, StringComparer.Ordinal)
            };
        }

        if (State.HiddenParameters.Count > 0)
        {
            working.HiddenParameters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                [State.Tool.Name] = new HashSet<string>(State.HiddenParameters, StringComparer.Ordinal)
            };
        }

        if (State.OptionOverrides.Count > 0)
        {
            working.ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                [State.Tool.Name] = new Dictionary<string, JsonElement>(State.OptionOverrides, StringComparer.Ordinal)
            };
        }

        return working;
    }

    private static string AddPinnedMarkers(string json, IEnumerable<string> pinnedKeys)
    {
        foreach (string key in pinnedKeys)
        {
            json = Regex.Replace(
                json,
                $@"(^\s*""{Regex.Escape(key)}"": .*?)(,?$)",
                "$1  // pinned",
                RegexOptions.Multiline);
        }

        return json;
    }

    /// <summary>
    /// Appends an annotation comment naming the parameters that were hidden for this
    /// tool, so the preview makes clear they were removed rather than never existing.
    /// The clipboard copy (<see cref="_cleanDefinitionJson"/>) stays valid JSON.
    /// </summary>
    private static string AddHiddenMarker(string json, IEnumerable<string> hiddenKeys)
    {
        List<string> hidden = hiddenKeys.ToList();
        if (hidden.Count == 0)
        {
            return json;
        }

        return json + Environment.NewLine + "// hidden parameters: " + string.Join(", ", hidden);
    }

    private void CopyDefinition_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_cleanDefinitionJson))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_cleanDefinitionJson);
            TestCallResultText.Text = "Copied tool definition to clipboard.";
        }
        catch (Exception)
        {
            TestCallResultText.Text = "Could not access the clipboard.";
        }
    }

    private async void TestCall_Click(object sender, RoutedEventArgs e)
    {
        TestCallResultText.Text = "Running…";
        TestCallButton.IsEnabled = false;
        try
        {
            IReadOnlyList<string> required = AgentToolRegistry.GetRequiredParameters(State.Tool);
            List<string> missing = required.Where(r => !State.PinnedParameters.ContainsKey(r)).ToList();
            if (missing.Count > 0)
            {
                TestCallResultText.Text = "Pin the required parameters first: " + string.Join(", ", missing) + ".";
                return;
            }

            JsonObject args = new();
            foreach ((string key, JsonElement value) in State.PinnedParameters)
            {
                args[key] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());
            }

            IReadOnlyDictionary<string, JsonElement> options = AgentToolContext.Resolve(State.Tool, _preset);
            using (AgentToolContext.Push(options))
            {
                ToolCallResult result = await State.Tool.ExecuteAsync(
                    JsonSerializer.SerializeToElement(args),
                    System.Threading.CancellationToken.None);
                TestCallResultText.Text = result.Success
                    ? $"OK — {result.Output}"
                    : $"Failed — {result.Error}";
            }
        }
        catch (Exception ex)
        {
            TestCallResultText.Text = "Test call error: " + ex.Message;
        }
        finally
        {
            TestCallButton.IsEnabled = true;
        }
    }

    // ── Schema helpers ─────────────────────────────────────────────

    private static string? GetPropertyString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static JsonElement GetSchemaDefault(JsonElement prop, string type)
    {
        if (prop.ValueKind == JsonValueKind.Object && prop.TryGetProperty("default", out JsonElement defaultValue))
        {
            return defaultValue.Clone();
        }

        return type switch
        {
            "boolean" => JsonSerializer.SerializeToElement(false),
            "integer" => JsonSerializer.SerializeToElement(0),
            "number" => JsonSerializer.SerializeToElement(0.0),
            _ => JsonSerializer.SerializeToElement(string.Empty)
        };
    }

    private static bool IsEnumProperty(JsonElement prop)
    {
        return prop.ValueKind == JsonValueKind.Object &&
               prop.TryGetProperty("enum", out JsonElement enumArray) &&
               enumArray.ValueKind == JsonValueKind.Array &&
               enumArray.GetArrayLength() > 0;
    }

    private static IReadOnlyList<string> GetEnumValues(JsonElement prop)
    {
        if (prop.ValueKind != JsonValueKind.Object || !prop.TryGetProperty("enum", out JsonElement enumArray))
        {
            return [];
        }

        return enumArray.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static Dictionary<string, string> GetEnumDescriptions(JsonElement prop)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (prop.ValueKind == JsonValueKind.Object && prop.TryGetProperty("x-enum-descriptions", out JsonElement descriptions) &&
            descriptions.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty item in descriptions.EnumerateObject())
            {
                if (item.Value.ValueKind == JsonValueKind.String)
                {
                    result[item.Name] = item.Value.GetString()!;
                }
            }
        }

        return result;
    }

    private static HashSet<string> GetRecommendedEngines(JsonElement prop)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (prop.ValueKind == JsonValueKind.Object && prop.TryGetProperty("x-enum-recommended", out JsonElement recommended) &&
            recommended.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in recommended.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(item.GetString()!);
                }
            }
        }

        return result;
    }

    private static bool HasEnginesAnnotation(JsonElement prop)
    {
        return prop.ValueKind == JsonValueKind.Object && prop.TryGetProperty("engines", out JsonElement engines) &&
               engines.ValueKind == JsonValueKind.Array;
    }

    private static bool HasEngine(JsonElement prop, string engine)
    {
        if (prop.ValueKind != JsonValueKind.Object || !prop.TryGetProperty("engines", out JsonElement engines) ||
            engines.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return engines.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String &&
                                                 string.Equals(e.GetString(), engine, StringComparison.Ordinal));
    }

    private int CountCustomizedOptions(JsonElement props, IReadOnlyDictionary<string, JsonElement> defaults)
    {
        int count = 0;
        foreach (JsonProperty prop in props.EnumerateObject())
        {
            if (prop.Name != "engine" && IsOptionCustomized(prop.Name, prop.Value, defaults))
            {
                count++;
            }
        }

        return count;
    }

    private bool IsOptionCustomized(string name, JsonElement prop, IReadOnlyDictionary<string, JsonElement> defaults)
    {
        if (!State.OptionOverrides.TryGetValue(name, out JsonElement overrideValue))
        {
            return false;
        }

        JsonElement defaultValue = defaults.TryGetValue(name, out JsonElement d) ? d : GetSchemaDefault(prop, GetPropertyString(prop, "type") ?? "string");
        return !JsonElementEquals(defaultValue, overrideValue);
    }

    private JsonElement GetEffectiveOption(string name, IReadOnlyDictionary<string, JsonElement> defaults)
    {
        if (State.OptionOverrides.TryGetValue(name, out JsonElement overrideValue))
        {
            return overrideValue.Clone();
        }

        return defaults.TryGetValue(name, out JsonElement d) ? d.Clone() : default;
    }

    private string GetEffectiveOptionString(string name, IReadOnlyDictionary<string, JsonElement> defaults)
    {
        JsonElement value = GetEffectiveOption(name, defaults);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        return a.GetRawText() == b.GetRawText();
    }

    private static string FormatOptionName(string name)
    {
        string[] parts = name.Split('_');
        return string.Join(" ", parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
