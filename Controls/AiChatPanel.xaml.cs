using KaneCode.Models;
using KaneCode.Services.Ai;
using KaneCode.Theming;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// AI Chat panel that streams completion tokens from an <see cref="IAiProvider"/>
/// and renders messages with basic markdown support (code blocks, inline code, bold).
/// </summary>
public partial class AiChatPanel : UserControl
{
    private readonly List<AiChatMessage> _conversationHistory = [];
    private readonly List<AiChatReference> _references = [];
    private readonly List<StreamSectionVisual> _streamSections = [];
    private IAiProvider? _provider;
    private AiProviderRegistry? _providerRegistry;
    private string? _model;
    private CancellationTokenSource? _modelDiscoveryCts;
    private CancellationTokenSource? _streamCts;
    private bool _isStreaming;
    private bool _isUpdatingModelListSelection;
    private AiUsageStats? _lastUsageStats;
    private Func<IReadOnlyList<ProjectItem>>? _projectItemsProvider;
    private Func<string?>? _projectConversationKeyProvider;
    private AgentToolRegistry? _toolRegistry;
    private AiChatModeRegistry? _modeRegistry;
    private AiDebugLogService? _debugLogService;
    private IAiChatMode? _activeMode;
    private ListBox? _mentionPopup;
    private string? _pendingSelectionContext;
    private bool _projectContextInjected;
    private const int DefaultOutboundTokenBudget = AiProviderSettings.DefaultContextLength;
    private const int MaxToolCallIterations = 40;

    private sealed class StreamSectionVisual(
        Border root,
        Border headerBar,
        TextBlock headerGlyph,
        TextBlock headerText,
        Border contentBorder,
        StackPanel contentPanel,
        Brush headerBackground,
        Brush contentBackground,
        Brush foreground,
        Brush borderBrush)
    {
        public Border Root { get; } = root;

        public Border HeaderBar { get; } = headerBar;

        public TextBlock HeaderGlyph { get; } = headerGlyph;

        public TextBlock HeaderText { get; } = headerText;

        public Border ContentBorder { get; } = contentBorder;

        public StackPanel ContentPanel { get; } = contentPanel;

        public Brush HeaderBackground { get; } = headerBackground;

        public Brush ContentBackground { get; } = contentBackground;

        public Brush Foreground { get; } = foreground;

        public Brush BorderBrush { get; } = borderBrush;

        public bool IsExpanded { get; set; }
    }

    private sealed class ToolCallSectionVisual(
        StreamSectionVisual section,
        TextBlock argumentsBlock,
        TextBlock resultBlock)
    {
        public StreamSectionVisual Section { get; } = section;

        public TextBlock ArgumentsBlock { get; } = argumentsBlock;

        public TextBlock ResultBlock { get; } = resultBlock;
    }

    public AiChatPanel()
    {
        InitializeComponent();
        ResetContextWindowBar();
        Loaded += AiChatPanel_Loaded;
        MessageScroller.ScrollChanged += MessageScroller_ScrollChanged;
        MessageScroller.SizeChanged += MessageScroller_SizeChanged;
    }

    /// <summary>
    /// Configures the provider and model to use for chat completions.
    /// </summary>
    internal void Configure(IAiProvider? provider, string? model = null)
    {
        _provider = provider;
        _model = model;
        _projectContextInjected = false;
    }

    internal static IReadOnlyList<string> BuildSelectableModelList(IReadOnlyList<string> discoveredModels, string? preferredModel)
    {
        ArgumentNullException.ThrowIfNull(discoveredModels);

        List<string> selectableModels = [];
        HashSet<string> seenModels = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preferredModel) && seenModels.Add(preferredModel))
        {
            selectableModels.Add(preferredModel);
        }

        foreach (string discoveredModel in discoveredModels)
        {
            if (string.IsNullOrWhiteSpace(discoveredModel) || !seenModels.Add(discoveredModel))
            {
                continue;
            }

            selectableModels.Add(discoveredModel);
        }

        return selectableModels.Count > 0 ? selectableModels : ["default"];
    }

    internal static string? SelectInitialModel(IReadOnlyList<string> availableModels, string? preferredModel)
    {
        ArgumentNullException.ThrowIfNull(availableModels);

        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            foreach (string availableModel in availableModels)
            {
                if (string.Equals(availableModel, preferredModel, StringComparison.OrdinalIgnoreCase))
                {
                    return availableModel;
                }
            }
        }

        return availableModels.Count > 0 ? availableModels[0] : null;
    }

    /// <summary>
    /// Sets the provider registry and populates the provider selector dropdown.
    /// The active provider is pre-selected.
    /// </summary>
    internal void SetProviderRegistry(AiProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (_providerRegistry is not null)
        {
            _providerRegistry.ProvidersChanged -= ProviderRegistry_ProvidersChanged;
        }

        _providerRegistry = registry;
        _providerRegistry.ProvidersChanged += ProviderRegistry_ProvidersChanged;
        RefreshProviderSelector();
    }

    /// <summary>
    /// Sets a callback that returns the current project items for file browsing and @ mentions.
    /// </summary>
    internal void SetProjectItemsProvider(Func<IReadOnlyList<ProjectItem>> provider)
    {
        _projectItemsProvider = provider;
    }

    /// <summary>
    /// Sets a callback that returns a stable key representing the active project/solution
    /// for persisted conversation history.
    /// </summary>
    internal void SetConversationProjectKeyProvider(Func<string?> provider)
    {
        _projectConversationKeyProvider = provider;
        TryLoadPersistedConversation();
    }

    /// <summary>
    /// Sets the tool registry for agent mode tool calling.
    /// </summary>
    internal void SetToolRegistry(AgentToolRegistry registry)
    {
        _toolRegistry = registry;
    }

    internal void SetDebugLogService(AiDebugLogService debugLogService)
    {
        ArgumentNullException.ThrowIfNull(debugLogService);

        _debugLogService = debugLogService;
    }

    /// <summary>
    /// Sets the available chat modes and populates the mode selector dropdown.
    /// The first registered mode is selected by default.
    /// </summary>
    internal void SetModeRegistry(AiChatModeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _modeRegistry = registry;
        ModeSelector.ItemsSource = registry.Modes;
        _activeMode = registry.Default;

        if (_activeMode is not null)
        {
            ModeSelector.SelectedItem = _activeMode;
        }
    }

    /// <summary>
    /// Prepares one-shot context for "Ask AI about selection" using the current file,
    /// selected code, and matching diagnostics. Context is injected into the next message only.
    /// </summary>
    internal void AskAboutSelection(string filePath, string selection, IReadOnlyList<DiagnosticItem> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Selection context:");
        sb.AppendLine($"File: {filePath}");
        sb.AppendLine();
        sb.AppendLine("Selected code:");
        sb.AppendLine("```csharp");
        sb.AppendLine(selection);
        sb.AppendLine("```");

        if (diagnostics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Diagnostics overlapping this selection:");

            foreach (var d in diagnostics)
            {
                sb.AppendLine($"- {d.Severity} {d.Code} at line {d.Line}, col {d.Column}: {d.Message}");
            }
        }

        _pendingSelectionContext = sb.ToString();

        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            InputBox.Text = "Can you explain this selection and suggest fixes if needed?";
            InputBox.CaretIndex = InputBox.Text.Length;
        }

        AppendSystemMessage("Selection context added for the next message.");
    }

    /// <summary>
    /// Focuses the chat input textbox and places caret at the end.
    /// </summary>
    internal void FocusInput()
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    internal static string GetDisplayedUserMessageContent(string typedText, string outboundText, bool showRawText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundText);

        return showRawText ? outboundText : typedText;
    }

    internal static string FormatRawTranscriptEntry(string label, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(content);

        return string.IsNullOrEmpty(content)
            ? $"{label}:"
            : $"{label}:\n{content}";
    }

    internal static string FormatDisplayedAssistantContent(string content, bool removeVerticalWhitespace)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!removeVerticalWhitespace)
        {
            return content;
        }

        return RemoveVerticalWhitespace(content);
    }

    internal static string RemoveVerticalWhitespace(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string normalizedContent = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        string[] lines = normalizedContent.Split('\n');
        List<string> compactedLines = [];
        bool inCodeBlock = false;

        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                compactedLines.Add(line);
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                compactedLines.Add(line);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                compactedLines.Add(line);
            }
        }

        return string.Join("\n", compactedLines);
    }

    // ── Conversation persistence and token budgeting ───────────────

    private void TryLoadPersistedConversation()
    {
        var key = _projectConversationKeyProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        try
        {
            var loaded = AiConversationStore.Load(key);
            _conversationHistory.Clear();
            _conversationHistory.AddRange(loaded);

            if (loaded.Count > 0)
            {
                AppendSystemMessage($"Loaded {loaded.Count} prior messages for this project.");
            }
        }
        catch (IOException ex)
        {
            AppendSystemMessage($"Could not load saved conversation history: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendSystemMessage($"Could not load saved conversation history: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            AppendSystemMessage($"Saved conversation history is invalid: {ex.Message}");
        }
        catch (JsonException ex)
        {
            AppendSystemMessage($"Saved conversation history JSON is invalid: {ex.Message}");
        }

        RefreshContextWindowDisplay();
    }

    private void SavePersistedConversation()
    {
        var key = _projectConversationKeyProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        try
        {
            AiConversationStore.Save(key, _conversationHistory);
        }
        catch (IOException ex)
        {
            AppendSystemMessage($"Could not save conversation history: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendSystemMessage($"Could not save conversation history: {ex.Message}");
        }
    }

    // ── Reference management ───────────────────────────────────────

    /// <summary>
    /// Adds a file reference to the current conversation context.
    /// </summary>
    internal void AddFileReference(string filePath)
    {
        if (_references.Any(r => r.FullPath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var reference = new AiChatReference(AiReferenceKind.File, filePath);

        try
        {
            reference.Content = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            reference.Content = "(unable to read file)";
        }

        _references.Add(reference);
        RenderReferenceTags();
    }

    private void RemoveReference(AiChatReference reference)
    {
        _references.Remove(reference);
        RenderReferenceTags();
    }

    private void RenderReferenceTags()
    {
        ReferenceTagsPanel.Children.Clear();

        foreach (var reference in _references)
        {
            var tag = CreateReferenceTag(reference);
            ReferenceTagsPanel.Children.Add(tag);
        }
    }

    private Border CreateReferenceTag(AiChatReference reference)
    {
        var removeButton = new Button
        {
            Content = "✕",
            FontSize = 9,
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(4, 0, 0, 0),
            Foreground = FindBrush("AiChatSecondaryForeground"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        removeButton.Click += (_, _) => RemoveReference(reference);

        var icon = reference.Kind == AiReferenceKind.File ? "📄 " : "🔗 ";

        var tag = new Border
        {
            Background = FindBrush("AiChatRefTagBackground"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 3, 2),
            Margin = new Thickness(0, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{icon}{reference.DisplayName}",
                        FontSize = 11,
                        Foreground = FindBrush("AiChatRefTagForeground"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    removeButton
                }
            }
        };

        tag.ToolTip = reference.FullPath;
        return tag;
    }

    /// <summary>
    /// Builds the context injection text from all attached references.
    /// </summary>
    private string BuildReferenceContext()
    {
        if (_references.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("The user has attached the following files for reference:");
        sb.AppendLine();

        foreach (var reference in _references)
        {
            sb.AppendLine(reference.ToContextString());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void AddReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        var projectItems = _projectItemsProvider?.Invoke();
        if (projectItems is null || projectItems.Count == 0)
        {
            AppendSystemMessage("No project loaded. Open a project or folder first.");
            return;
        }

        var dialog = new AiReferencePickerDialog(projectItems)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.SelectedFilePaths)
            {
                AddFileReference(path);
            }
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Handle @ mention popup navigation
        if (_mentionPopup is not null && _mentionPopup.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (_mentionPopup.SelectedIndex < _mentionPopup.Items.Count - 1)
                {
                    _mentionPopup.SelectedIndex++;
                }

                return;
            }

            if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (_mentionPopup.SelectedIndex > 0)
                {
                    _mentionPopup.SelectedIndex--;
                }

                return;
            }

            if (e.Key is Key.Enter or Key.Tab)
            {
                e.Handled = true;
                AcceptMentionSelection();
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DismissMentionPopup();
                return;
            }
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;

        // Shift+Enter inserts a newline
        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            e.Handled = true;
            var caretIndex = InputBox.CaretIndex;
            InputBox.Text = InputBox.Text.Insert(caretIndex, Environment.NewLine);
            InputBox.CaretIndex = caretIndex + Environment.NewLine.Length;
            return;
        }

        // Enter sends (without Shift)
        if (modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    // ── @ mention autocomplete ─────────────────────────────────────

    /// <summary>
    /// Called when text changes in the input box. Detects '@' triggers for file mention autocomplete.
    /// </summary>
    internal void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text;
        var caret = InputBox.CaretIndex;

        if (string.IsNullOrEmpty(text) || caret == 0)
        {
            DismissMentionPopup();
            return;
        }

        // Find the '@' before the caret
        var atIndex = text.LastIndexOf('@', caret - 1);
        if (atIndex < 0 || (atIndex > 0 && !char.IsWhiteSpace(text[atIndex - 1])))
        {
            DismissMentionPopup();
            return;
        }

        var query = text[(atIndex + 1)..caret];

        // Don't show popup if query contains whitespace (user moved on)
        if (query.Contains(' ') || query.Contains('\n'))
        {
            DismissMentionPopup();
            return;
        }

        ShowMentionPopup(query, atIndex);
    }

    private void ShowMentionPopup(string query, int atIndex)
    {
        var projectItems = _projectItemsProvider?.Invoke();
        if (projectItems is null || projectItems.Count == 0)
        {
            DismissMentionPopup();
            return;
        }

        var allFiles = new List<string>();
        CollectFilePaths(projectItems, allFiles);

        var filtered = string.IsNullOrEmpty(query)
            ? allFiles.Take(12).ToList()
            : allFiles.Where(p =>
                Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase))
              .Take(12).ToList();

        if (filtered.Count == 0)
        {
            DismissMentionPopup();
            return;
        }

        if (_mentionPopup is null)
        {
            _mentionPopup = new ListBox
            {
                MaxHeight = 180,
                FontSize = 11,
                Background = FindBrush("AiChatInputBackground"),
                Foreground = FindBrush("AiChatForeground"),
                BorderBrush = FindBrush("AiChatBorder"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 0, 4, 2)
            };

            _mentionPopup.MouseDoubleClick += (_, _) => AcceptMentionSelection();

            // Insert popup just above the input area border
            var inputBorder = InputBox.Parent as DockPanel;
            var inputContainer = inputBorder?.Parent as Border;
            if (inputContainer is not null)
            {
                var parentPanel = inputContainer.Parent as DockPanel;
                if (parentPanel is not null)
                {
                    var idx = parentPanel.Children.IndexOf(inputContainer);
                    parentPanel.Children.Insert(idx, _mentionPopup);
                    DockPanel.SetDock(_mentionPopup, Dock.Bottom);
                }
            }
        }

        _mentionPopup.ItemsSource = filtered.Select(Path.GetFileName).ToList();
        _mentionPopup.Tag = filtered; // Store full paths
        _mentionPopup.SelectedIndex = 0;
        _mentionPopup.Visibility = Visibility.Visible;
    }

    private void AcceptMentionSelection()
    {
        if (_mentionPopup is null || _mentionPopup.SelectedIndex < 0)
        {
            return;
        }

        var fullPaths = _mentionPopup.Tag as List<string>;
        if (fullPaths is null || _mentionPopup.SelectedIndex >= fullPaths.Count)
        {
            return;
        }

        var selectedPath = fullPaths[_mentionPopup.SelectedIndex];
        var fileName = Path.GetFileName(selectedPath);

        // Replace the @query with the filename and add the file as a reference
        var text = InputBox.Text;
        var caret = InputBox.CaretIndex;
        var atIndex = text.LastIndexOf('@', caret - 1);

        if (atIndex >= 0)
        {
            var newText = text[..atIndex] + fileName + (caret < text.Length ? text[caret..] : "");
            InputBox.Text = newText;
            InputBox.CaretIndex = atIndex + fileName.Length;
        }

        AddFileReference(selectedPath);
        DismissMentionPopup();
    }

    private void DismissMentionPopup()
    {
        if (_mentionPopup is not null)
        {
            _mentionPopup.Visibility = Visibility.Collapsed;
        }
    }

    private static void CollectFilePaths(IReadOnlyList<ProjectItem> items, List<string> results)
    {
        foreach (var item in items)
        {
            if (item.ItemType == ProjectItemType.File)
            {
                results.Add(item.FullPath);
            }

            if (item.Children.Count > 0)
            {
                CollectFilePaths(item.Children, results);
            }
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        CancelStreaming();
        _conversationHistory.Clear();
        _streamSections.Clear();
        MessagePanel.Children.Clear();
        PinnedSectionPanel.Children.Clear();
        PinnedSectionPanel.Visibility = Visibility.Collapsed;
        _pendingSelectionContext = null;
        _projectContextInjected = false;
        ResetContextWindowBar();

        var key = _projectConversationKeyProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                AiConversationStore.Clear(key);
            }
            catch (IOException ex)
            {
                AppendSystemMessage($"Could not clear conversation history: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                AppendSystemMessage($"Could not clear conversation history: {ex.Message}");
            }
        }

        RefreshContextWindowDisplay();
    }

    private async void ProviderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderSelector.SelectedItem is not IAiProvider selected)
        {
            return;
        }

        _providerRegistry?.SetActiveProvider(selected);

        AiProviderSettings? matchingSettings = _providerRegistry?.GetSettings(selected);
        Configure(selected, matchingSettings?.SelectedModel);
        await RefreshModelListAsync(selected, matchingSettings?.SelectedModel);
    }

    private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingModelListSelection || ModelSelector.SelectedItem is not string selectedModel)
        {
            return;
        }

        _model = selectedModel;
    }

    /// <summary>
    /// Refreshes the provider selector dropdown from the current registry state.
    /// </summary>
    private async void RefreshProviderSelector()
    {
        if (_providerRegistry is null)
        {
            return;
        }

        ProviderSelector.SelectionChanged -= ProviderSelector_SelectionChanged;
        ProviderSelector.ItemsSource = _providerRegistry.Providers;

        IAiProvider? active = _providerRegistry.ActiveProvider;
        if (active is not null)
        {
            AiProviderSettings? activeSettings = _providerRegistry.GetSettings(active);
            ProviderSelector.SelectedItem = active;
            Configure(active, activeSettings?.SelectedModel);
            await RefreshModelListAsync(active, activeSettings?.SelectedModel);
        }
        else if (_providerRegistry.Providers.Count > 0)
        {
            IAiProvider firstProvider = _providerRegistry.Providers[0];
            AiProviderSettings? firstProviderSettings = _providerRegistry.GetSettings(firstProvider);
            ProviderSelector.SelectedIndex = 0;
            Configure(firstProvider, firstProviderSettings?.SelectedModel);
            await RefreshModelListAsync(firstProvider, firstProviderSettings?.SelectedModel);
        }
        else
        {
            ProviderSelector.SelectedItem = null;
            Configure(null);
            await RefreshModelListAsync(null, null);
        }

        ProviderSelector.SelectionChanged += ProviderSelector_SelectionChanged;
    }

    private void ProviderRegistry_ProvidersChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshProviderSelector);
            return;
        }

        RefreshProviderSelector();
    }

    private async Task RefreshModelListAsync(IAiProvider? provider, string? preferredModel)
    {
        CancelModelDiscovery();

        if (provider is null)
        {
            ApplyModelList([], null);
            return;
        }

        IReadOnlyList<string> fallbackModels = BuildSelectableModelList(provider.AvailableModels, preferredModel ?? _model);
        ApplyModelList(fallbackModels, preferredModel ?? _model);

        CancellationTokenSource modelDiscoveryCts = new();
        _modelDiscoveryCts = modelDiscoveryCts;

        IReadOnlyList<string> discoveredModels;
        try
        {
            discoveredModels = await provider.GetAvailableModelsAsync(modelDiscoveryCts.Token);
        }
        catch (OperationCanceledException) when (modelDiscoveryCts.IsCancellationRequested)
        {
            return;
        }

        if (!ReferenceEquals(_modelDiscoveryCts, modelDiscoveryCts) || !ReferenceEquals(_provider, provider))
        {
            return;
        }

        IReadOnlyList<string> selectableModels = BuildSelectableModelList(discoveredModels, preferredModel ?? _model);
        ApplyModelList(selectableModels, preferredModel ?? _model);
    }

    private void ApplyModelList(IReadOnlyList<string> models, string? preferredModel)
    {
        ArgumentNullException.ThrowIfNull(models);

        _isUpdatingModelListSelection = true;
        try
        {
            ModelSelector.ItemsSource = models;
            ModelSelector.IsEnabled = models.Count > 0;
            string? selectedModel = SelectInitialModel(models, preferredModel);
            ModelSelector.SelectedItem = selectedModel;
            _model = selectedModel;
        }
        finally
        {
            _isUpdatingModelListSelection = false;
        }
    }

    private void CancelModelDiscovery()
    {
        if (_modelDiscoveryCts is null)
        {
            return;
        }

        _modelDiscoveryCts.Cancel();
        _modelDiscoveryCts.Dispose();
        _modelDiscoveryCts = null;
    }

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeSelector.SelectedItem is not IAiChatMode mode)
        {
            return;
        }

        _activeMode = mode;
        AppendSystemMessage($"Switched to {mode.DisplayName} mode.");
        RefreshContextWindowDisplay();
    }

    private async Task SendMessageAsync()
    {
        string? text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_provider is null || !_provider.IsConfigured)
        {
            AppendSystemMessage("No AI provider configured. Go to Options → AI Providers.");
            return;
        }

        if (_isStreaming)
        {
            return;
        }

        InputBox.Text = string.Empty;

        string outboundUserContent;

        string displayedUserContent;

        // Inject project-wide system context once per conversation
        if (!_projectContextInjected)
        {
            IReadOnlyList<ProjectItem> projectItems = _projectItemsProvider?.Invoke() ?? [];
            string projectContext = AiProjectContextBuilder.Build(projectItems);

            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                _conversationHistory.Add(new AiChatMessage(AiChatRole.System, projectContext));
            }

            _projectContextInjected = true;
        }

        // Add to history with context injection (references + one-shot selection context)
        string referenceContext = BuildReferenceContext();

        System.Text.StringBuilder combinedContext = new();
        if (!string.IsNullOrEmpty(referenceContext))
        {
            combinedContext.AppendLine(referenceContext);
        }

        if (!string.IsNullOrEmpty(_pendingSelectionContext))
        {
            combinedContext.AppendLine(_pendingSelectionContext);
            _pendingSelectionContext = null; // one-shot context
        }

        outboundUserContent = combinedContext.Length > 0
            ? $"{combinedContext}\n{text}"
            : text;

        displayedUserContent = GetDisplayedUserMessageContent(text, outboundUserContent, IsRawTextModeEnabled());

        AppendUserMessage(displayedUserContent);

        _conversationHistory.Add(new AiChatMessage(AiChatRole.User, outboundUserContent));

        SavePersistedConversation();

        // Stream assistant response
        _isStreaming = true;
        SendButton.IsEnabled = true;
        SendButton.Content = "⏹ Stop";
        SendButton.Click -= SendButton_Click;
        SendButton.Click += StopButton_Click;

        var reasoningTokenCount = 0;
        var contentTokenCount = 0;
        var streamStopwatch = Stopwatch.StartNew();

        CancelStreaming();
        _streamCts = new CancellationTokenSource();
        CancellationToken ct = _streamCts.Token;
        bool cutoffMarkerAddedForRequest = false;
        bool rawTextMode = IsRawTextModeEnabled();
        bool streamingDisabled = IsStreamingDisabled();
        bool streamResponses = !streamingDisabled;
        bool rawSystemPromptAddedForRequest = false;

        try
        {
            string model = _model ?? _provider.AvailableModels.FirstOrDefault() ?? "default";
            JsonElement toolsDef = _activeMode is not null && _activeMode.ToolsEnabled && _toolRegistry is not null
                ? _activeMode.GetToolDefinitions(_toolRegistry)
                : default;

            int iteration = 0;

            while (iteration < MaxToolCallIterations)
            {
                iteration++;
                ct.ThrowIfCancellationRequested();

                System.Text.StringBuilder responseBuilder = new();
                System.Text.StringBuilder reasoningBuilder = new();
                StreamSectionVisual? thinkingSection = null;
                TextBlock? thinkingTextBlock = null;
                TextBlock? rawThinkingBlock = null;
                Dictionary<int, AiStreamToolCall> streamedToolCalls = new();
                Dictionary<int, ToolCallSectionVisual> toolCallBlocks = new();
                Dictionary<int, TextBlock> rawToolCallBlocks = new();
                Dictionary<int, ToolCallSectionVisual> malformedToolCallBlocks = new();
                Dictionary<int, TextBlock> rawMalformedToolCallBlocks = new();
                bool sawRealToolCallToken = false;

                // UI element creation must happen on the dispatcher thread.
                // After the first iteration, we may be on a thread-pool thread
                // due to ConfigureAwait(false) in tool execution.
                (StackPanel assistantContainer, RichTextBox assistantBlock) = await Dispatcher.InvokeAsync(CreateAssistantMessageBlock);

                bool toolsEnabled = _activeMode?.ToolsEnabled == true;
                int outboundTokenBudget = GetOutboundTokenBudget();
                AiContextWindowSnapshot contextWindow = AiContextWindowBuilder.Build(_conversationHistory, outboundTokenBudget, toolsEnabled);
                IReadOnlyList<AiChatMessage> outboundMessages = BuildOutboundMessages(contextWindow.Messages, toolsDef);

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateContextWindowBar(contextWindow.Info);

                    if (rawTextMode &&
                        !rawSystemPromptAddedForRequest &&
                        outboundMessages.Count > 0 &&
                        outboundMessages[0].Role == AiChatRole.System)
                    {
                        AppendRawTranscriptEntry("System Prompt", outboundMessages[0].Content, FindBrush("AiChatSecondaryForeground"), assistantContainer);
                        rawSystemPromptAddedForRequest = true;
                    }

                    if (contextWindow.Info.CutoffOccurred && !cutoffMarkerAddedForRequest)
                    {
                        AppendContextWindowCutoffMarker(contextWindow.Info);
                        cutoffMarkerAddedForRequest = true;
                    }
                });

                await foreach (AiStreamToken token in _provider.StreamCompletionAsync(outboundMessages, model, toolsDef, streamResponses, ct)
                    .ConfigureAwait(false))
                {
                    if (token.Type == AiStreamTokenType.Usage)
                    {
                        _lastUsageStats = token.UsageStats;
                        continue;
                    }

                    if (token.Type == AiStreamTokenType.ToolCall && token.ToolCall is not null)
                    {
                        AiStreamToolCall toolCall = token.ToolCall!;
                        if (!sawRealToolCallToken)
                        {
                            sawRealToolCallToken = true;

                            if (!streamingDisabled)
                            {
                                await Dispatcher.InvokeAsync(() =>
                                    ClearMalformedToolCallPreviewBlocks(malformedToolCallBlocks, rawMalformedToolCallBlocks));
                            }
                        }

                        streamedToolCalls[toolCall.Index] = toolCall;

                        if (streamingDisabled)
                        {
                            continue;
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            bool shouldStickToBottom = IsMessageScrollerNearBottom();

                            if (rawTextMode)
                            {
                                string rawToolCallText = FormatRawTranscriptEntry(
                                    $"Tool Call ({toolCall.FunctionName})",
                                    FormatToolArgs(toolCall.ArgumentsJson));

                                if (!rawToolCallBlocks.TryGetValue(toolCall.Index, out TextBlock? rawBlock))
                                {
                                    rawBlock = AppendRawTranscriptEntry(
                                        $"Tool Call ({toolCall.FunctionName})",
                                        FormatToolArgs(toolCall.ArgumentsJson),
                                        FindBrush(ThemeResourceKeys.AiChatToolCallForeground),
                                        assistantContainer);
                                    rawToolCallBlocks[toolCall.Index] = rawBlock;
                                }
                                else
                                {
                                    rawBlock.Text = rawToolCallText;
                                }

                                if (shouldStickToBottom)
                                {
                                    MessageScroller.ScrollToEnd();
                                }

                                return;
                            }

                            if (!toolCallBlocks.TryGetValue(toolCall.Index, out ToolCallSectionVisual? block))
                            {
                                block = CreateToolCallBlock(
                                    toolCall.FunctionName,
                                    toolCall.ArgumentsJson,
                                    assistantContainer,
                                    assistantBlock);
                                toolCallBlocks[toolCall.Index] = block;
                            }
                            else
                            {
                                UpdateToolCallBlock(block, toolCall.FunctionName, toolCall.ArgumentsJson);
                            }

                            if (shouldStickToBottom)
                            {
                                MessageScroller.ScrollToEnd();
                            }
                        });

                        continue;
                    }

                    if (token.Type == AiStreamTokenType.Reasoning)
                    {
                        reasoningBuilder.Append(token.Text);
                        reasoningTokenCount++;

                        if (streamingDisabled)
                        {
                            continue;
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            bool shouldStickToBottom = IsMessageScrollerNearBottom();

                            if (rawTextMode)
                            {
                                if (rawThinkingBlock is null)
                                {
                                    rawThinkingBlock = AppendRawTranscriptEntry(
                                        "Thinking",
                                        reasoningBuilder.ToString(),
                                        FindBrush("AiChatThinkingForeground"),
                                        assistantContainer);
                                }
                                else
                                {
                                    rawThinkingBlock.Text = FormatRawTranscriptEntry("Thinking", reasoningBuilder.ToString());
                                }

                                if (!sawRealToolCallToken && _activeMode?.ToolsEnabled == true)
                                {
                                    UpdateMalformedToolCallPreviewBlocks(
                                        reasoningBuilder.ToString(),
                                        responseBuilder.ToString(),
                                        malformedToolCallBlocks,
                                        rawMalformedToolCallBlocks,
                                        rawTextMode,
                                        assistantContainer,
                                        assistantBlock);
                                }

                                UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                                if (shouldStickToBottom)
                                {
                                    MessageScroller.ScrollToEnd();
                                }

                                return;
                            }

                            if (thinkingSection is null)
                            {
                                (thinkingSection, thinkingTextBlock) = CreateThinkingSection(assistantContainer, assistantBlock);
                            }

                            thinkingTextBlock!.Text = FormatDisplayedAssistantContent(
                                reasoningBuilder.ToString(),
                                ShouldRemoveVerticalWhitespace());
                            thinkingTextBlock.Visibility = string.IsNullOrWhiteSpace(thinkingTextBlock.Text)
                                ? Visibility.Collapsed
                                : Visibility.Visible;

                            if (!sawRealToolCallToken && _activeMode?.ToolsEnabled == true)
                            {
                                UpdateMalformedToolCallPreviewBlocks(
                                    reasoningBuilder.ToString(),
                                    responseBuilder.ToString(),
                                    malformedToolCallBlocks,
                                    rawMalformedToolCallBlocks,
                                    rawTextMode,
                                    assistantContainer,
                                    assistantBlock);
                            }

                            SetInlineSectionHeader(thinkingSection, $"Thinking ({reasoningTokenCount:N0} tokens)...");
                            UpdatePinnedSectionHeaders();

                            UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                            if (shouldStickToBottom)
                            {
                                MessageScroller.ScrollToEnd();
                            }
                        });
                    }
                    else
                    {
                        responseBuilder.Append(token.Text);
                        contentTokenCount++;

                        if (streamingDisabled)
                        {
                            continue;
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            bool shouldStickToBottom = IsMessageScrollerNearBottom();

                            // Finalize the thinking header once content starts
                            if (thinkingSection is not null &&
                                thinkingSection.HeaderText.Text.EndsWith("...", StringComparison.Ordinal))
                            {
                                SetInlineSectionHeader(thinkingSection, $"Thought for {reasoningTokenCount:N0} tokens");
                            }

                            if (!sawRealToolCallToken && _activeMode?.ToolsEnabled == true)
                            {
                                UpdateMalformedToolCallPreviewBlocks(
                                    reasoningBuilder.ToString(),
                                    responseBuilder.ToString(),
                                    malformedToolCallBlocks,
                                    rawMalformedToolCallBlocks,
                                    rawTextMode,
                                    assistantContainer,
                                    assistantBlock);
                            }

                            RenderAssistantContent(assistantBlock, responseBuilder.ToString());
                            UpdatePinnedSectionHeaders();
                            UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                            if (shouldStickToBottom)
                            {
                                MessageScroller.ScrollToEnd();
                            }
                        });
                    }
                }

                if (streamingDisabled)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        bool shouldStickToBottom = IsMessageScrollerNearBottom();

                        if (rawTextMode)
                        {
                            if (!string.IsNullOrWhiteSpace(reasoningBuilder.ToString()))
                            {
                                AppendRawTranscriptEntry(
                                    "Thinking",
                                    reasoningBuilder.ToString(),
                                    FindBrush("AiChatThinkingForeground"),
                                    assistantContainer);
                            }

                            if (!string.IsNullOrWhiteSpace(responseBuilder.ToString()))
                            {
                                AppendRawTranscriptEntry(
                                    "Assistant",
                                    responseBuilder.ToString(),
                                    FindBrush("AiChatAssistantForeground"),
                                    assistantContainer);
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(reasoningBuilder.ToString()))
                            {
                                (thinkingSection, thinkingTextBlock) = CreateThinkingSection(assistantContainer, assistantBlock);
                                thinkingTextBlock.Text = FormatDisplayedAssistantContent(
                                    reasoningBuilder.ToString(),
                                    ShouldRemoveVerticalWhitespace());
                                thinkingTextBlock.Visibility = Visibility.Visible;
                                SetInlineSectionHeader(
                                    thinkingSection,
                                    reasoningTokenCount > 0
                                        ? $"Thought for {reasoningTokenCount:N0} tokens"
                                        : "Thought");
                            }

                            RenderAssistantContent(assistantBlock, responseBuilder.ToString());
                            UpdatePinnedSectionHeaders();
                        }

                        UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                        if (shouldStickToBottom)
                        {
                            MessageScroller.ScrollToEnd();
                        }
                    });
                }

                if (thinkingSection is not null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (thinkingSection.HeaderText.Text.EndsWith("...", StringComparison.Ordinal))
                        {
                            SetInlineSectionHeader(
                                thinkingSection,
                                reasoningTokenCount > 0
                                    ? $"Thought for {reasoningTokenCount:N0} tokens"
                                    : "Thought");
                        }
                    });
                }

                List<RecoveredMalformedToolCall> recoveredMalformedToolCalls = _activeMode?.ToolsEnabled == true && streamedToolCalls.Count == 0
                    ? MalformedToolCallRecovery.Recover(reasoningBuilder.ToString(), responseBuilder.ToString()).ToList()
                    : [];

                if (rawTextMode &&
                    string.IsNullOrWhiteSpace(responseBuilder.ToString()) &&
                    streamedToolCalls.Count == 0 &&
                    recoveredMalformedToolCalls.Count == 0)
                {
                    await Dispatcher.InvokeAsync(() => MessagePanel.Children.Remove(assistantContainer));
                }

                // If tool calls were requested, execute them and loop
                if (_activeMode?.ToolsEnabled == true && streamedToolCalls.Count > 0 && _toolRegistry is not null)
                {
                    List<AiStreamToolCall> pendingToolCalls = streamedToolCalls
                        .OrderBy(kv => kv.Key)
                        .Select(kv => kv.Value)
                        .Where(tc => !string.IsNullOrWhiteSpace(tc.FunctionName))
                        .ToList();

                    if (pendingToolCalls.Count == 0)
                    {
                        _conversationHistory.Add(new AiChatMessage(AiChatRole.Assistant, responseBuilder.ToString())
                        {
                            ThinkingContent = reasoningBuilder.ToString()
                        });
                        SavePersistedConversation();
                        break;
                    }

                    // Record the assistant message with its tool calls
                    List<AiToolCallRequest> toolCallRequests = pendingToolCalls
                        .Select(tc =>
                        {
                            string toolCallId = string.IsNullOrWhiteSpace(tc.Id)
                                ? $"tool_call_{tc.Index}"
                                : tc.Id;

                            return new AiToolCallRequest(toolCallId, tc.FunctionName, tc.ArgumentsJson);
                        })
                        .ToList();

                    _conversationHistory.Add(new AiChatMessage(AiChatRole.Assistant, responseBuilder.ToString())
                    {
                        ThinkingContent = reasoningBuilder.ToString(),
                        ToolCalls = toolCallRequests
                    });

                    // Execute each tool call and append results
                    foreach (AiStreamToolCall toolCall in pendingToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();

                        string toolCallId = string.IsNullOrWhiteSpace(toolCall.Id)
                            ? $"tool_call_{toolCall.Index}"
                            : toolCall.Id;

                        ToolCallSectionVisual? toolCallBlock = null;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (rawTextMode)
                            {
                                if (!rawToolCallBlocks.TryGetValue(toolCall.Index, out TextBlock? rawBlock))
                                {
                                    rawBlock = AppendRawTranscriptEntry(
                                        $"Tool Call ({toolCall.FunctionName})",
                                        FormatToolArgs(toolCall.ArgumentsJson),
                                        FindBrush(ThemeResourceKeys.AiChatToolCallForeground),
                                        assistantContainer);
                                    rawToolCallBlocks[toolCall.Index] = rawBlock;
                                }

                                return;
                            }

                            if (!toolCallBlocks.TryGetValue(toolCall.Index, out ToolCallSectionVisual? block))
                            {
                                block = CreateToolCallBlock(
                                    toolCall.FunctionName,
                                    toolCall.ArgumentsJson,
                                    assistantContainer,
                                    assistantBlock);
                                toolCallBlocks[toolCall.Index] = block;
                            }
                            else
                            {
                                UpdateToolCallBlock(block, toolCall.FunctionName, toolCall.ArgumentsJson);
                            }

                            toolCallBlock = block;
                        });

                        IAgentTool? tool = _activeMode?.IsToolAllowed(toolCall.FunctionName) == true
                            ? _toolRegistry.Get(toolCall.FunctionName)
                            : null;
                        ToolCallResult result;

                        if (tool is null)
                        {
                            result = ToolCallResult.Fail($"Unknown or disallowed tool: {toolCall.FunctionName}");
                        }
                        else
                        {
                            try
                            {
                                JsonElement args = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
                                    ? default
                                    : JsonDocument.Parse(toolCall.ArgumentsJson).RootElement;

                                result = await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                result = ToolCallResult.Fail($"Tool execution error: {ex.Message}");
                            }
                        }

                        await LogToolFailureAsync(toolCall.FunctionName, toolCallId, toolCall.ArgumentsJson, result);

                        string resultContent = result.Success
                            ? result.Output
                            : $"Error: {result.Error}";

                        _conversationHistory.Add(new AiChatMessage(AiChatRole.Tool, resultContent)
                        {
                            ToolCallId = toolCallId
                        });

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (rawTextMode)
                            {
                                AppendRawTranscriptEntry(
                                    $"Tool Result ({toolCall.FunctionName})",
                                    result.Success ? result.Output : result.Error ?? "Unknown error",
                                    result.Success
                                        ? FindBrush(ThemeResourceKeys.AiChatToolCallSuccessForeground)
                                        : FindBrush(ThemeResourceKeys.AiChatToolCallErrorForeground),
                                    assistantContainer);
                                return;
                            }

                            FinalizeToolCallBlock(toolCallBlock!, toolCall.FunctionName, result);
                        });
                    }

                    SavePersistedConversation();

                    // Continue the loop — the next iteration will re-send to the model
                    continue;
                }

                if (_activeMode?.ToolsEnabled == true && recoveredMalformedToolCalls.Count > 0)
                {
                    List<AiToolCallRequest> toolCallRequests = recoveredMalformedToolCalls
                        .Select(tc => new AiToolCallRequest($"malformed_tool_call_{iteration}_{tc.Index}", tc.FunctionName, tc.ArgumentsJson))
                        .ToList();

                    _conversationHistory.Add(new AiChatMessage(AiChatRole.Assistant, responseBuilder.ToString())
                    {
                        ThinkingContent = reasoningBuilder.ToString(),
                        ToolCalls = toolCallRequests
                    });

                    foreach (RecoveredMalformedToolCall recoveredToolCall in recoveredMalformedToolCalls)
                    {
                        string toolCallId = $"malformed_tool_call_{iteration}_{recoveredToolCall.Index}";
                        ToolCallSectionVisual? toolCallBlock = null;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (rawTextMode)
                            {
                                if (!rawMalformedToolCallBlocks.TryGetValue(recoveredToolCall.Index, out TextBlock? rawBlock))
                                {
                                    rawBlock = AppendRawTranscriptEntry(
                                        $"Tool Call ({recoveredToolCall.FunctionName})",
                                        FormatToolArgs(recoveredToolCall.ArgumentsJson),
                                        FindBrush(ThemeResourceKeys.AiChatToolCallForeground),
                                        assistantContainer);
                                    rawMalformedToolCallBlocks[recoveredToolCall.Index] = rawBlock;
                                }
                                else
                                {
                                    rawBlock.Text = FormatRawTranscriptEntry(
                                        $"Tool Call ({recoveredToolCall.FunctionName})",
                                        FormatToolArgs(recoveredToolCall.ArgumentsJson));
                                }

                                return;
                            }

                            ToolCallSectionVisual? existingBlock;
                            ToolCallSectionVisual block;
                            if (!malformedToolCallBlocks.TryGetValue(recoveredToolCall.Index, out existingBlock))
                            {
                                block = CreateToolCallBlock(
                                    recoveredToolCall.FunctionName,
                                    recoveredToolCall.ArgumentsJson,
                                    assistantContainer,
                                    assistantBlock);
                                malformedToolCallBlocks[recoveredToolCall.Index] = block;
                            }
                            else
                            {
                                block = existingBlock;
                                UpdateToolCallBlock(block, recoveredToolCall.FunctionName, recoveredToolCall.ArgumentsJson);
                            }

                            toolCallBlock = block;
                        });

                        ToolCallResult result = ToolCallResult.Fail(recoveredToolCall.Error);
                        await LogToolFailureAsync(recoveredToolCall.FunctionName, toolCallId, recoveredToolCall.ArgumentsJson, result);

                        _conversationHistory.Add(new AiChatMessage(AiChatRole.Tool, $"Error: {recoveredToolCall.Error}")
                        {
                            ToolCallId = toolCallId
                        });

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (rawTextMode)
                            {
                                AppendRawTranscriptEntry(
                                    $"Tool Result ({recoveredToolCall.FunctionName})",
                                    recoveredToolCall.Error,
                                    FindBrush(ThemeResourceKeys.AiChatToolCallErrorForeground),
                                    assistantContainer);
                                return;
                            }

                            FinalizeToolCallBlock(toolCallBlock!, recoveredToolCall.FunctionName, result);
                        });
                    }

                    SavePersistedConversation();
                    continue;
                }

                // No tool calls — this is the final content response
                _conversationHistory.Add(new AiChatMessage(AiChatRole.Assistant, responseBuilder.ToString())
                {
                    ThinkingContent = reasoningBuilder.ToString()
                });
                SavePersistedConversation();
                break;
            }

            if (iteration >= MaxToolCallIterations)
            {
                await Dispatcher.InvokeAsync(() =>
                    AppendSystemMessage($"⚠️ Tool-call loop reached maximum iterations ({MaxToolCallIterations})."));
            }

            streamStopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            // User cancelled — nothing extra to record
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => AppendSystemMessage($"Error: {ex.Message}"));
        }
        finally
        {
            streamStopwatch.Stop();
            var finalTokenCount = reasoningTokenCount + contentTokenCount;

            await Dispatcher.InvokeAsync(() =>
            {
                _isStreaming = false;
                SendButton.Content = "Send";
                SendButton.IsEnabled = true;
                SendButton.Click -= StopButton_Click;
                SendButton.Click += SendButton_Click;

                UpdateStatsBarFinal(finalTokenCount, reasoningTokenCount, contentTokenCount, streamStopwatch);
                RefreshContextWindowDisplay();
            });
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "…";
    }

    private async Task LogToolFailureAsync(string toolName, string toolCallId, string? argumentsJson, ToolCallResult result)
    {
        if (result.Success || _debugLogService is null || string.IsNullOrWhiteSpace(result.Error))
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
            _debugLogService.LogToolFailure(toolName, argumentsJson, result.Error, toolCallId));
    }

    /// <summary>
    /// Merges the active mode's system prompt (if any) with any existing system
    /// messages from the context window into a single system message at position 0.
    /// Many inference servers (e.g. llama.cpp) require exactly one system message
    /// at the beginning of the conversation.
    /// </summary>
    private IReadOnlyList<AiChatMessage> BuildOutboundMessages(IReadOnlyList<AiChatMessage> outboundWindow, JsonElement toolsDef)
    {
        ArgumentNullException.ThrowIfNull(outboundWindow);

        var modePrompt = _activeMode?.BuildSystemPrompt(toolsDef);

        // Collect all leading system messages from the context window so we can
        // merge them with the mode prompt into a single system message.
        var systemParts = new List<string>();
        var nonSystemStartIndex = 0;

        if (!string.IsNullOrWhiteSpace(modePrompt))
        {
            systemParts.Add(modePrompt);
        }

        for (var i = 0; i < outboundWindow.Count; i++)
        {
            if (outboundWindow[i].Role == AiChatRole.System)
            {
                if (!string.IsNullOrWhiteSpace(outboundWindow[i].Content))
                {
                    systemParts.Add(outboundWindow[i].Content);
                }

                nonSystemStartIndex = i + 1;
            }
            else
            {
                break;
            }
        }

        if (systemParts.Count == 0)
        {
            return outboundWindow;
        }

        var mergedSystem = string.Join("\n\n", systemParts);
        var messages = new List<AiChatMessage>(outboundWindow.Count - nonSystemStartIndex + 1)
        {
            new(AiChatRole.System, mergedSystem)
        };

        for (var i = nonSystemStartIndex; i < outboundWindow.Count; i++)
        {
            messages.Add(outboundWindow[i]);
        }

        return messages;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        CancelStreaming();
    }

    private void CancelStreaming()
    {
        if (_streamCts is not null)
        {
            _streamCts.Cancel();
            _streamCts.Dispose();
            _streamCts = null;
        }
    }

    // ── Stats bar ──────────────────────────────────────────────────

    /// <summary>
    /// Updates the stats bar during streaming with live token count and tokens/sec.
    /// </summary>
    private void UpdateStatsBar(int totalTokens, Stopwatch stopwatch)
    {
        double elapsed = stopwatch.Elapsed.TotalSeconds;
        double tokPerSec = elapsed > 0.1 ? totalTokens / elapsed : 0;
        StatsBar.Text = $"{totalTokens:N0} tokens  •  {tokPerSec:F1} tok/s";
    }

    /// <summary>
    /// Updates the stats bar with final summary after streaming completes.
    /// Shows prompt tokens (context), completion breakdown, and tokens/sec.
    /// </summary>
    private void UpdateStatsBarFinal(int totalGenerated, int reasoningTokens, int contentTokens, Stopwatch stopwatch)
    {
        double elapsed = stopwatch.Elapsed.TotalSeconds;
        double tokPerSec = elapsed > 0.1 ? totalGenerated / elapsed : 0;

        List<string> parts = new();

        if (_lastUsageStats is { } usage)
        {
            parts.Add($"ctx: {usage.PromptTokens:N0}");
        }

        if (reasoningTokens > 0)
        {
            parts.Add($"think: {reasoningTokens:N0}");
        }

        parts.Add($"out: {contentTokens:N0}");
        parts.Add($"{tokPerSec:F1} tok/s");
        parts.Add($"{elapsed:F1}s");

        StatsBar.Text = string.Join("  •  ", parts);
    }

    private void ResetContextWindowBar()
    {
        ContextWindowBar.Text = $"window: 0 msgs  •  est: 0/{GetOutboundTokenBudget():N0} tok";
        ContextWindowBar.Foreground = FindBrush("AiChatSecondaryForeground");
        ContextWindowBar.ToolTip = "Estimated conversation history that will be sent with the next request.";
    }

    private void RefreshContextWindowDisplay()
    {
        bool includeToolMessages = _activeMode?.ToolsEnabled == true;
        AiContextWindowSnapshot snapshot = AiContextWindowBuilder.Build(_conversationHistory, GetOutboundTokenBudget(), includeToolMessages);
        UpdateContextWindowBar(snapshot.Info);
    }

    private int GetOutboundTokenBudget()
    {
        AiProviderSettings? settings = _provider is null
            ? null
            : _providerRegistry?.GetSettings(_provider);

        if (settings?.ContextLength is int contextLength && contextLength > 0)
        {
            return contextLength;
        }

        return DefaultOutboundTokenBudget;
    }

    private void UpdateContextWindowBar(AiContextWindowInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        List<string> parts =
        [
            $"window: {info.IncludedMessages}/{info.TotalConsideredMessages} msgs",
            $"est: {info.SelectedTokens:N0}/{info.BudgetTokens:N0} tok"
        ];

        if (info.CutoffOccurred)
        {
            parts.Add($"cutoff: {info.DroppedMessages} dropped");
        }
        else
        {
            parts.Add("cutoff: none");
        }

        if (info.ExcludedMessages > 0)
        {
            parts.Add($"hidden: {info.ExcludedMessages}");
        }

        ContextWindowBar.Text = string.Join("  •  ", parts);
        ContextWindowBar.Foreground = info.CutoffOccurred
            ? Brushes.IndianRed
            : FindBrush("AiChatSecondaryForeground");
        ContextWindowBar.ToolTip = info.CutoffOccurred
            ? $"Estimated conversation window: {info.SelectedTokens:N0}/{info.BudgetTokens:N0} tokens. {info.DroppedMessages} earlier message(s) were omitted from the outbound request."
            : $"Estimated conversation window: {info.SelectedTokens:N0}/{info.BudgetTokens:N0} tokens. No history was trimmed from the outbound request.";
    }

    private void AppendContextWindowCutoffMarker(AiContextWindowInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        string omittedMessage = info.DroppedMessages == 1
            ? "1 earlier message was omitted from this request."
            : $"{info.DroppedMessages} earlier messages were omitted from this request.";

        if (IsRawTextModeEnabled())
        {
            AppendRawTranscriptEntry("System", $"Context window cutoff — {omittedMessage}", Brushes.IndianRed);
            return;
        }

        Border marker = new()
        {
            Margin = new Thickness(8, 12, 8, 8),
            Padding = new Thickness(0, 6, 0, 0),
            BorderBrush = Brushes.IndianRed,
            BorderThickness = new Thickness(0, 2, 0, 0),
            Child = new TextBlock
            {
                Text = $"Context window cutoff — {omittedMessage}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.IndianRed,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            },
            ToolTip = $"Estimated conversation window: {info.SelectedTokens:N0}/{info.BudgetTokens:N0} tokens."
        };

        MessagePanel.Children.Add(marker);
        MessageScroller.ScrollToEnd();
    }

    // ── Message rendering ──────────────────────────────────────────

    private void AppendUserMessage(string text)
    {
        if (IsRawTextModeEnabled())
        {
            AppendRawTranscriptEntry("User", text, FindBrush("AiChatUserForeground"));
            return;
        }

        Border border = new()
        {
            Background = FindBrush("AiChatUserBubble"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(40, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        RichTextBox rtb = new()
        {
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = FindBrush("AiChatUserForeground"),
            FontSize = 13,
            Padding = new Thickness(0),
            IsDocumentEnabled = true
        };

        RenderPlainTextInto(rtb, text, FindBrush("AiChatUserForeground"), 18, useMonospace: IsRawTextModeEnabled());
        border.Child = rtb;
        MessagePanel.Children.Add(border);
        MessageScroller.ScrollToEnd();
    }

    /// <summary>
    /// Creates an empty assistant message container and returns the RichTextBox used for progressive rendering.
    /// Thinking and tool call sections are inserted into the same container so the stream stays in order.
    /// </summary>
    private (StackPanel container, RichTextBox contentBlock) CreateAssistantMessageBlock()
    {
        StackPanel container = new()
        {
            Margin = new Thickness(0, 4, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        RichTextBox rtb = new()
        {
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = FindBrush("AiChatAssistantForeground"),
            FontSize = 14,
            FontFamily = new FontFamily("Segoe UI"),
            IsDocumentEnabled = true,
            Margin = new Thickness(8, 0, 8, 0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        rtb.Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            LineHeight = 20
        };

        container.Children.Add(rtb);
        MessagePanel.Children.Add(container);
        return (container, rtb);
    }

    private bool IsRawTextModeEnabled()
    {
        return RawTextCheckBox.IsChecked == true;
    }

    private bool ShouldAutoExpandThinkingSections()
    {
        return AutoExpandThinkingCheckBox.IsChecked == true;
    }

    private bool ShouldAutoExpandToolSections()
    {
        return AutoExpandToolsCheckBox.IsChecked == true;
    }

    private bool ShouldRemoveVerticalWhitespace()
    {
        return RemoveVerticalWhitespaceCheckBox.IsChecked == true;
    }

    private bool IsStreamingDisabled()
    {
        return DisableStreamingCheckBox.IsChecked == true;
    }

    private void RenderAssistantContent(RichTextBox richTextBox, string content)
    {
        ArgumentNullException.ThrowIfNull(richTextBox);
        ArgumentNullException.ThrowIfNull(content);

        if (IsRawTextModeEnabled())
        {
            RenderPlainTextInto(
                richTextBox,
                FormatRawTranscriptEntry("Assistant", content),
                FindBrush("AiChatAssistantForeground"),
                20,
                useMonospace: true);
            return;
        }

        string formattedContent = FormatDisplayedAssistantContent(content, ShouldRemoveVerticalWhitespace());
        RenderMarkdownInto(richTextBox, formattedContent);
    }

    private static void RenderPlainTextInto(RichTextBox richTextBox, string text, Brush foreground, double lineHeight, bool useMonospace)
    {
        ArgumentNullException.ThrowIfNull(richTextBox);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(foreground);

        FlowDocument document = new()
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            LineHeight = lineHeight
        };

        Paragraph paragraph = new()
        {
            Margin = new Thickness(0),
            Foreground = foreground
        };
        paragraph.Inlines.Add(new Run(text));
        document.Blocks.Add(paragraph);

        richTextBox.FontFamily = useMonospace
            ? new FontFamily("Cascadia Code, Consolas, Courier New")
            : new FontFamily("Segoe UI");
        richTextBox.Document = document;
    }

    /// <summary>
    /// Creates a subtle collapsible section for streamed thinking content.
    /// </summary>
    private (StreamSectionVisual section, TextBlock textBlock) CreateThinkingSection(Panel hostPanel, UIElement insertBefore)
    {
        Brush thinkingBackground = FindBrush("AiChatThinkingBackground");
        Brush thinkingForeground = FindBrush("AiChatThinkingForeground");
        Brush thinkingBorder = FindBrush("AiChatThinkingBorder");
        StreamSectionVisual section = CreateInlineSection(
            "Thinking...",
            thinkingBackground,
            thinkingBackground,
            thinkingForeground,
            thinkingBorder,
            hostPanel,
            insertBefore);

        TextBlock textBlock = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = thinkingForeground,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Visibility = Visibility.Collapsed
        };

        section.ContentPanel.Children.Add(textBlock);
        SetInlineSectionExpanded(section, isExpanded: ShouldAutoExpandThinkingSections());
        return (section, textBlock);
    }

    private StreamSectionVisual CreateInlineSection(
        string header,
        Brush headerBackground,
        Brush contentBackground,
        Brush foreground,
        Brush borderBrush,
        Panel hostPanel,
        UIElement insertBefore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        ArgumentNullException.ThrowIfNull(headerBackground);
        ArgumentNullException.ThrowIfNull(contentBackground);
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(borderBrush);
        ArgumentNullException.ThrowIfNull(hostPanel);
        ArgumentNullException.ThrowIfNull(insertBefore);

        TextBlock glyphBlock;
        TextBlock headerTextBlock;
        Border headerBar = CreateSectionHeaderBar(header, foreground, headerBackground, borderBrush, out glyphBlock, out headerTextBlock);

        StackPanel contentPanel = new();
        Border contentBorder = new()
        {
            Background = contentBackground,
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Child = contentPanel
        };

        StackPanel sectionLayout = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        sectionLayout.Children.Add(headerBar);
        sectionLayout.Children.Add(contentBorder);

        Border root = new()
        {
            Background = contentBackground,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 4, 0, 0),
            Child = sectionLayout
        };

        StreamSectionVisual section = new(
            root,
            headerBar,
            glyphBlock,
            headerTextBlock,
            contentBorder,
            contentPanel,
            headerBackground,
            contentBackground,
            foreground,
            borderBrush);

        headerBar.MouseLeftButtonUp += (_, _) => ToggleInlineSection(section);
        InsertBefore(hostPanel, root, insertBefore);
        _streamSections.Add(section);
        UpdateInlineSectionState(section);
        return section;
    }

    private static Border CreateSectionHeaderBar(
        string title,
        Brush foreground,
        Brush background,
        Brush borderBrush,
        out TextBlock glyphBlock,
        out TextBlock titleBlock)
    {
        glyphBlock = new TextBlock
        {
            Text = "▸",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = foreground
        };

        titleBlock = new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = foreground
        };

        StackPanel headerContent = new()
        {
            Orientation = Orientation.Horizontal
        };
        headerContent.Children.Add(glyphBlock);
        headerContent.Children.Add(titleBlock);

        return new Border
        {
            Background = background,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            Cursor = Cursors.Hand,
            Child = headerContent
        };
    }

    private static void InsertBefore(Panel hostPanel, UIElement element, UIElement insertBefore)
    {
        ArgumentNullException.ThrowIfNull(hostPanel);
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(insertBefore);

        int index = hostPanel.Children.IndexOf(insertBefore);
        if (index >= 0)
        {
            hostPanel.Children.Insert(index, element);
            return;
        }

        hostPanel.Children.Add(element);
    }

    private void ToggleInlineSection(StreamSectionVisual section)
    {
        ArgumentNullException.ThrowIfNull(section);
        SetInlineSectionExpanded(section, !section.IsExpanded);
    }

    private void SetInlineSectionExpanded(StreamSectionVisual section, bool isExpanded)
    {
        ArgumentNullException.ThrowIfNull(section);
        section.IsExpanded = isExpanded;
        UpdateInlineSectionState(section);
        UpdatePinnedSectionHeaders();
    }

    private void SetInlineSectionHeader(StreamSectionVisual section, string header)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        section.HeaderText.Text = header;
        UpdatePinnedSectionHeaders();
    }

    private static void UpdateInlineSectionState(StreamSectionVisual section)
    {
        ArgumentNullException.ThrowIfNull(section);

        section.ContentBorder.Visibility = section.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        section.HeaderGlyph.Text = section.IsExpanded ? "▾" : "▸";
        section.HeaderBar.BorderThickness = section.IsExpanded ? new Thickness(0, 0, 0, 1) : new Thickness(0);
    }

    private void AppendSystemMessage(string text)
    {
        if (IsRawTextModeEnabled())
        {
            AppendRawTranscriptEntry("System", text, FindBrush("AiChatSecondaryForeground"));
            return;
        }

        TextBlock tb = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            Foreground = FindBrush("AiChatSecondaryForeground"),
            FontSize = 12,
            Margin = new Thickness(8, 4, 8, 4)
        };

        MessagePanel.Children.Add(tb);
        MessageScroller.ScrollToEnd();
    }

    private TextBlock AppendRawTranscriptEntry(string label, string content, Brush foreground, UIElement? insertBefore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(foreground);

        TextBlock textBlock = new()
        {
            Text = FormatRawTranscriptEntry(label, content),
            TextWrapping = TextWrapping.Wrap,
            Foreground = foreground,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            Margin = new Thickness(8, 4, 8, 4)
        };

        if (insertBefore is not null)
        {
            int index = MessagePanel.Children.IndexOf(insertBefore);
            if (index >= 0)
            {
                MessagePanel.Children.Insert(index, textBlock);
            }
            else
            {
                MessagePanel.Children.Add(textBlock);
            }
        }
        else
        {
            MessagePanel.Children.Add(textBlock);
        }

        MessageScroller.ScrollToEnd();
        return textBlock;
    }

    /// <summary>
    /// Creates a collapsible tool-call block with a tool-title header, streamed call content, and result content.
    /// </summary>
    private ToolCallSectionVisual CreateToolCallBlock(
        string toolName,
        string argumentsJson,
        Panel hostPanel,
        UIElement insertBefore)
    {
        Brush toolBackground = FindBrush(ThemeResourceKeys.AiChatToolCallBackground);
        Brush toolForeground = FindBrush(ThemeResourceKeys.AiChatToolCallForeground);
        Brush toolBorder = FindBrush(ThemeResourceKeys.AiChatToolCallBorder);
        StreamSectionVisual section = CreateInlineSection(
            FormatToolCallHeader(toolName),
            toolBackground,
            toolBackground,
            toolForeground,
            toolBorder,
            hostPanel,
            insertBefore);

        TextBlock argumentsBlock = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = toolForeground,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New")
        };

        TextBlock resultBlock = new()
        {
            Text = "Running tool...",
            TextWrapping = TextWrapping.Wrap,
            Foreground = toolForeground,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New")
        };

        UpdateToolCallArgumentsContent(argumentsBlock, resultBlock, argumentsJson);
        section.ContentPanel.Children.Add(argumentsBlock);
        section.ContentPanel.Children.Add(resultBlock);
        SetInlineSectionExpanded(section, isExpanded: ShouldAutoExpandToolSections());

        bool shouldStickToBottom = IsMessageScrollerNearBottom();
        if (shouldStickToBottom)
        {
            MessageScroller.ScrollToEnd();
        }

        return new ToolCallSectionVisual(section, argumentsBlock, resultBlock);
    }

    /// <summary>
    /// Updates a tool-call block while the call arguments are still streaming.
    /// </summary>
    private void UpdateToolCallBlock(ToolCallSectionVisual block, string toolName, string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(block);
        SetInlineSectionHeader(block.Section, FormatToolCallHeader(toolName));
        UpdateToolCallArgumentsContent(block.ArgumentsBlock, block.ResultBlock, argumentsJson);
    }

    /// <summary>
    /// Updates a tool-call block with the final result (success or error).
    /// </summary>
    private void FinalizeToolCallBlock(ToolCallSectionVisual block, string toolName, ToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(block);
        SetInlineSectionHeader(block.Section, FormatToolCallHeader(toolName));

        if (result.Success)
        {
            block.ResultBlock.Text = result.Output;
            block.ResultBlock.Foreground = FindBrush(ThemeResourceKeys.AiChatToolCallSuccessForeground);
        }
        else
        {
            block.ResultBlock.Text = result.Error ?? "Unknown error";
            block.ResultBlock.Foreground = FindBrush(ThemeResourceKeys.AiChatToolCallErrorForeground);
        }

        bool shouldStickToBottom = IsMessageScrollerNearBottom();
        UpdatePinnedSectionHeaders();

        if (shouldStickToBottom)
        {
            MessageScroller.ScrollToEnd();
        }
    }

    private void UpdateMalformedToolCallPreviewBlocks(
        string reasoningContent,
        string responseContent,
        Dictionary<int, ToolCallSectionVisual> malformedToolCallBlocks,
        Dictionary<int, TextBlock> rawMalformedToolCallBlocks,
        bool rawTextMode,
        Panel hostPanel,
        UIElement insertBefore)
    {
        ArgumentNullException.ThrowIfNull(malformedToolCallBlocks);
        ArgumentNullException.ThrowIfNull(rawMalformedToolCallBlocks);
        ArgumentNullException.ThrowIfNull(hostPanel);
        ArgumentNullException.ThrowIfNull(insertBefore);

        IReadOnlyList<StreamingMalformedToolCall> detectedToolCalls = MalformedToolCallRecovery.DetectStreaming(reasoningContent, responseContent);
        foreach (StreamingMalformedToolCall detectedToolCall in detectedToolCalls)
        {
            if (rawTextMode)
            {
                string label = $"Tool Call ({detectedToolCall.FunctionName})";
                if (!rawMalformedToolCallBlocks.TryGetValue(detectedToolCall.Index, out TextBlock? rawBlock))
                {
                    rawBlock = AppendRawTranscriptEntry(
                        label,
                        detectedToolCall.RawText,
                        FindBrush(ThemeResourceKeys.AiChatToolCallForeground),
                        hostPanel);
                    rawMalformedToolCallBlocks[detectedToolCall.Index] = rawBlock;
                }
                else
                {
                    rawBlock.Text = FormatRawTranscriptEntry(label, detectedToolCall.RawText);
                }

                continue;
            }

            if (!malformedToolCallBlocks.TryGetValue(detectedToolCall.Index, out ToolCallSectionVisual? block))
            {
                block = CreateToolCallBlock(detectedToolCall.FunctionName, string.Empty, hostPanel, insertBefore);
                malformedToolCallBlocks[detectedToolCall.Index] = block;
            }

            UpdateMalformedToolCallBlock(block, detectedToolCall.FunctionName, detectedToolCall.RawText);
        }
    }

    private void ClearMalformedToolCallPreviewBlocks(
        Dictionary<int, ToolCallSectionVisual> malformedToolCallBlocks,
        Dictionary<int, TextBlock> rawMalformedToolCallBlocks)
    {
        ArgumentNullException.ThrowIfNull(malformedToolCallBlocks);
        ArgumentNullException.ThrowIfNull(rawMalformedToolCallBlocks);

        foreach (ToolCallSectionVisual block in malformedToolCallBlocks.Values)
        {
            RemoveInlineSection(block.Section);
        }

        malformedToolCallBlocks.Clear();

        foreach (TextBlock rawBlock in rawMalformedToolCallBlocks.Values)
        {
            if (rawBlock.Parent is Panel parentPanel)
            {
                parentPanel.Children.Remove(rawBlock);
            }
        }

        rawMalformedToolCallBlocks.Clear();
        UpdatePinnedSectionHeaders();
    }

    private void RemoveInlineSection(StreamSectionVisual section)
    {
        ArgumentNullException.ThrowIfNull(section);

        if (section.Root.Parent is Panel parentPanel)
        {
            parentPanel.Children.Remove(section.Root);
        }

        _streamSections.Remove(section);
    }

    private void UpdateMalformedToolCallBlock(ToolCallSectionVisual block, string toolName, string rawText)
    {
        ArgumentNullException.ThrowIfNull(block);
        SetInlineSectionHeader(block.Section, FormatToolCallHeader(toolName));
        UpdateToolCallBodyContent(block.ArgumentsBlock, block.ResultBlock, rawText);
    }

    /// <summary>
    /// Formats tool arguments JSON into a readable display string.
    /// </summary>
    private static string FormatToolArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "(no arguments)";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(argumentsJson);
            List<string> entries = [];
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                string? value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();

                entries.Add($"{prop.Name}: {value ?? ""}");
            }

            return string.Join("\n", entries);
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    internal static string FormatToolCallBody(string argumentsJson)
    {
        return string.IsNullOrWhiteSpace(argumentsJson)
            ? string.Empty
            : FormatToolArgs(argumentsJson);
    }

    internal static string FormatToolCallHeader(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return Truncate(toolName, 180);
    }

    internal static IReadOnlyList<int> GetPinnedSectionIndexes(
        IReadOnlyList<(double Top, double Bottom, bool IsExpanded)> sectionBounds,
        double pinLine)
    {
        ArgumentNullException.ThrowIfNull(sectionBounds);

        List<int> pinnedIndexes = [];
        for (int i = 0; i < sectionBounds.Count; i++)
        {
            (double Top, double Bottom, bool IsExpanded) section = sectionBounds[i];
            if (section.IsExpanded && section.Top < pinLine && section.Bottom > pinLine)
            {
                pinnedIndexes.Add(i);
            }
        }

        return pinnedIndexes;
    }

    private static void UpdateToolCallArgumentsContent(TextBlock argumentsBlock, TextBlock resultBlock, string argumentsJson)
    {
        UpdateToolCallBodyContent(argumentsBlock, resultBlock, FormatToolCallBody(argumentsJson));
    }

    private static void UpdateToolCallBodyContent(TextBlock argumentsBlock, TextBlock resultBlock, string content)
    {
        ArgumentNullException.ThrowIfNull(argumentsBlock);
        ArgumentNullException.ThrowIfNull(resultBlock);
        ArgumentNullException.ThrowIfNull(content);

        bool hasContent = !string.IsNullOrWhiteSpace(content);
        argumentsBlock.Text = content;
        argumentsBlock.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        resultBlock.Margin = hasContent ? new Thickness(0, 6, 0, 0) : new Thickness(0);
    }

    private void AiChatPanel_Loaded(object sender, RoutedEventArgs e)
    {
        UpdatePinnedSectionHeaders();
    }

    private void MessageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            UpdatePinnedSectionHeaders();
        }
    }

    private void MessageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePinnedSectionHeaders();
    }

    private void UpdatePinnedSectionHeaders()
    {
        if (!IsLoaded)
        {
            return;
        }

        List<StreamSectionVisual> measuredSections = [];
        List<(double Top, double Bottom, bool IsExpanded)> sectionBounds = [];

        foreach (StreamSectionVisual section in _streamSections)
        {
            if (section.Root.Parent is null || section.Root.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (!section.Root.IsLoaded || !section.Root.IsArrangeValid)
            {
                continue;
            }

            GeneralTransform transform = section.Root.TransformToAncestor(MessageScroller);
            Point topLeft = transform.Transform(new Point(0, 0));
            double top = topLeft.Y;
            double bottom = top + section.Root.ActualHeight;
            measuredSections.Add(section);
            sectionBounds.Add((top, bottom, section.IsExpanded));
        }

        IReadOnlyList<int> pinnedIndexes = GetPinnedSectionIndexes(sectionBounds, 0.0);
        PinnedSectionPanel.Children.Clear();

        foreach (int pinnedIndex in pinnedIndexes)
        {
            Border pinnedHeader = CreatePinnedHeader(measuredSections[pinnedIndex]);
            PinnedSectionPanel.Children.Add(pinnedHeader);
        }

        PinnedSectionPanel.Visibility = pinnedIndexes.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Border CreatePinnedHeader(StreamSectionVisual section)
    {
        ArgumentNullException.ThrowIfNull(section);

        TextBlock glyphBlock;
        TextBlock titleBlock;
        Border pinnedHeader = CreateSectionHeaderBar(
            section.HeaderText.Text,
            section.Foreground,
            section.HeaderBackground,
            section.BorderBrush,
            out glyphBlock,
            out titleBlock);

        pinnedHeader.Margin = new Thickness(0);
        pinnedHeader.HorizontalAlignment = HorizontalAlignment.Stretch;
        pinnedHeader.BorderThickness = new Thickness(0, 1, 0, 1);
        glyphBlock.Text = section.HeaderGlyph.Text;
        pinnedHeader.MouseLeftButtonUp += (_, _) => ToggleInlineSection(section);
        return pinnedHeader;
    }

    /// <summary>
    /// Returns true when the message scroller is at (or near) the bottom.
    /// This allows users to scroll up during streaming without being forced back down,
    /// while still auto-following new content when already near the end.
    /// </summary>
    private bool IsMessageScrollerNearBottom()
    {
        const double autoFollowThreshold = 24.0;
        var remaining = MessageScroller.ScrollableHeight - MessageScroller.VerticalOffset;
        return remaining <= autoFollowThreshold;
    }

    // ── Markdown rendering ─────────────────────────────────────────

    /// <summary>
    /// Parses markdown text and renders it into the given <see cref="RichTextBox"/>.
    /// Supports fenced code blocks, tables, inline code, and bold text.
    /// </summary>
    private void RenderMarkdownInto(RichTextBox rtb, string markdown)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };

        var lines = markdown.Split('\n');
        var inCodeBlock = false;
        var codeLanguage = string.Empty;
        var codeLines = new List<string>();
        var tableLines = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Fenced code block toggle
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushTable(doc, tableLines);

                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeLanguage = line.TrimStart()[3..].Trim();
                    codeLines.Clear();
                }
                else
                {
                    // End of code block — render accumulated code
                    doc.Blocks.Add(CreateCodeBlock(string.Join('\n', codeLines)));
                    inCodeBlock = false;
                    codeLines.Clear();
                }

                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            // Table row detection — lines that start and end with '|'
            if (IsTableRow(line))
            {
                tableLines.Add(line);
                continue;
            }

            // Non-table line encountered — flush any accumulated table first
            FlushTable(doc, tableLines);

            // Normal paragraph
            var paragraph = CreateMarkdownParagraph(line);
            doc.Blocks.Add(paragraph);
        }

        // Flush any trailing table or unclosed code block
        FlushTable(doc, tableLines);

        if (inCodeBlock && codeLines.Count > 0)
        {
            doc.Blocks.Add(CreateCodeBlock(string.Join('\n', codeLines)));
        }

        rtb.Document = doc;
    }

    // ── Table rendering ───────────────────────────────────────────

    /// <summary>
    /// Returns true if the line looks like a markdown table row (starts and contains '|').
    /// </summary>
    private static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.Contains('|', StringComparison.Ordinal) && trimmed.Length > 1;
    }

    /// <summary>
    /// Returns true if the line is a separator row (e.g. "|---|---|").
    /// </summary>
    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Length > 0 && trimmed.Replace("-", "").Replace("|", "").Replace(":", "").Replace(" ", "").Length == 0;
    }

    /// <summary>
    /// Splits a markdown table row into cell values, trimming outer pipes and whitespace.
    /// </summary>
    private static string[] SplitTableRow(string line)
    {
        var trimmed = line.Trim();

        // Strip leading and trailing '|'
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    /// <summary>
    /// If <paramref name="tableLines"/> has accumulated rows, renders them as a
    /// <see cref="Table"/> block and clears the list.
    /// </summary>
    private void FlushTable(FlowDocument doc, List<string> tableLines)
    {
        if (tableLines.Count == 0)
        {
            return;
        }

        var borderBrush = FindBrush("AiChatBorder");
        var headerBg = FindBrush("AiChatCodeBlockBackground");
        var foreground = FindBrush("AiChatAssistantForeground");

        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 4),
            Foreground = foreground
        };

        // Determine column count from the first row
        var firstRowCells = SplitTableRow(tableLines[0]);
        foreach (var _ in firstRowCells)
        {
            table.Columns.Add(new TableColumn());
        }

        var rowGroup = new TableRowGroup();
        var isFirstDataRow = true;

        foreach (var rowLine in tableLines)
        {
            // Skip separator rows (|---|---|)
            if (IsTableSeparator(rowLine))
            {
                continue;
            }

            var cells = SplitTableRow(rowLine);
            var tableRow = new TableRow();

            for (var i = 0; i < firstRowCells.Length; i++)
            {
                var cellText = i < cells.Length ? cells[i] : string.Empty;
                var paragraph = new Paragraph { Margin = new Thickness(0), Foreground = foreground };
                ParseInlineMarkdown(paragraph, cellText);

                var cell = new TableCell(paragraph)
                {
                    Padding = new Thickness(6, 3, 6, 3),
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1)
                };

                if (isFirstDataRow)
                {
                    cell.Background = headerBg;
                    paragraph.FontWeight = FontWeights.SemiBold;
                }

                tableRow.Cells.Add(cell);
            }

            rowGroup.Rows.Add(tableRow);
            isFirstDataRow = false;
        }

        table.RowGroups.Add(rowGroup);
        doc.Blocks.Add(table);
        tableLines.Clear();
    }

    /// <summary>
    /// Creates a styled code block paragraph.
    /// </summary>
    private Paragraph CreateCodeBlock(string code)
    {
        var paragraph = new Paragraph
        {
            Background = FindBrush("AiChatCodeBlockBackground"),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 0, 6),
            Foreground = FindBrush("AiChatCodeForeground")
        };

        paragraph.Inlines.Add(new Run(code));
        return paragraph;
    }

    /// <summary>
    /// Creates a paragraph with inline markdown formatting (bold, inline code, headings).
    /// </summary>
    private Paragraph CreateMarkdownParagraph(string line)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 3, 0, 3),
            LineHeight = 20,
            Foreground = FindBrush("AiChatAssistantForeground")
        };

        // Headings
        if (line.StartsWith("### ", StringComparison.Ordinal))
        {
            paragraph.FontSize = 14;
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.Margin = new Thickness(0, 6, 0, 2);
            line = line[4..];
        }
        else if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            paragraph.FontSize = 15;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Margin = new Thickness(0, 8, 0, 2);
            line = line[3..];
        }
        else if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            paragraph.FontSize = 16;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Margin = new Thickness(0, 10, 0, 2);
            line = line[2..];
        }

        // Bullet list
        if (line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("* ", StringComparison.Ordinal))
        {
            paragraph.TextIndent = 0;
            paragraph.Margin = new Thickness(12, 1, 0, 1);
            line = "• " + line[2..];
        }

        // Parse inline formatting
        ParseInlineMarkdown(paragraph, line);

        return paragraph;
    }

    /// <summary>
    /// Parses inline markdown: **bold**, `code`, and plain text segments.
    /// </summary>
    private void ParseInlineMarkdown(Paragraph paragraph, string text)
    {
        // Pattern: **bold** or `code`
        var pattern = @"(\*\*(.+?)\*\*)|(`(.+?)`)";
        var lastIndex = 0;

        foreach (Match match in Regex.Matches(text, pattern))
        {
            // Text before the match
            if (match.Index > lastIndex)
            {
                paragraph.Inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            if (match.Groups[2].Success)
            {
                // Bold
                paragraph.Inlines.Add(new Bold(new Run(match.Groups[2].Value)));
            }
            else if (match.Groups[4].Success)
            {
                // Inline code
                var codeRun = new Run(match.Groups[4].Value)
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                    Background = FindBrush("AiChatCodeBlockBackground"),
                    Foreground = FindBrush("AiChatCodeForeground"),
                    FontSize = 12
                };
                paragraph.Inlines.Add(codeRun);
            }

            lastIndex = match.Index + match.Length;
        }

        // Remaining text
        if (lastIndex < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private Brush FindBrush(string key)
    {
        return TryFindResource(key) is Brush b ? b : Brushes.Gray;
    }

    // ── Conversation export ───────────────────────────────────────

    private void MessageArea_SaveConversation(object sender, RoutedEventArgs e)
    {
        if (_conversationHistory.Count == 0)
        {
            AppendSystemMessage("No conversation to save.");
            return;
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var fileName = $"ai-conversation_{timestamp}.txt";
            var tempPath = Path.Combine(Path.GetTempPath(), fileName);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"AI Chat Conversation Export — {DateTime.Now:g}");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            if (ProviderSelector.SelectedItem is IAiProvider selectedProvider)
            {
                sb.AppendLine($"Provider: {selectedProvider.DisplayName}");
                sb.AppendLine();
            }

            var messageCount = 0;
            foreach (var message in _conversationHistory)
            {
                sb.AppendLine($"[{message.Role}]");
                sb.AppendLine("───────────────────────────────────────────────────────────────");
                sb.AppendLine(message.Content);
                sb.AppendLine();
                messageCount++;
            }

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"Total messages: {messageCount}");
            sb.AppendLine($"Exported: {DateTime.Now:g}");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");

            File.WriteAllText(tempPath, sb.ToString(), System.Text.Encoding.UTF8);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true
                }
            };
            process.Start();

            AppendSystemMessage($"Conversation saved and opened: {fileName}");
        }
        catch (Exception ex)
        {
            AppendSystemMessage($"Failed to save conversation: {ex.Message}");
        }
    }
}
