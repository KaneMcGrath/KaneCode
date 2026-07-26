using KaneCode.Models;
using KaneCode.Services;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Agents;
using KaneCode.Services.Ai.Modes;
using KaneCode.Theming;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KaneCode.Controls;

/// <summary>
/// AI Chat panel that streams completion tokens from an <see cref="IAiProvider"/>
/// and renders messages with basic markdown support (code blocks, inline code, bold).
/// </summary>
public partial class AiChatPanel : UserControl
{
    private readonly AiConversationState _conversationState = new();
    private readonly List<StreamSectionVisual> _streamSections = [];
    private IAiProvider? _provider;
    private AiProviderRegistry? _providerRegistry;
    private string? _model;
    private CancellationTokenSource? _modelDiscoveryCts;
    private CancellationTokenSource? _streamCts;
    private bool _isStreaming;
    private AiUsageStats? _aggregatedUsageStats;
    private Func<IReadOnlyList<ProjectItem>>? _projectItemsProvider;
    private Func<string?>? _projectConversationKeyProvider;
    private Func<AiContextDocumentSnapshot?>? _currentDocumentProvider;
    private Func<IReadOnlyList<AiContextDocumentSnapshot>>? _openDocumentsProvider;
    private Func<AiBuildOutputSnapshot?>? _buildOutputProvider;
    private AgentToolRegistry? _toolRegistry;
    private AiChatModeRegistry? _modeRegistry;
    private AiDebugLogService? _debugLogService;
    private ExternalContextDirectoryRegistry? _externalContextDirectoryRegistry;
    private Services.Ai.Agents.AgentOrchestrator? _agentOrchestrator;

    /// <summary>
    /// When non-null, the chat panel is displaying the conversation of a specific
    /// agent (identified by this ID) rather than the normal <see cref="AiConversation"/>.
    /// Set to null to restore the normal conversation view.
    /// </summary>
    private string? _viewingAgentId;

    /// <summary>
    /// Guard flag to prevent <see cref="AgentSessionSelector_SelectionChanged"/>
    /// from triggering view switches during programmatic refreshes of the dropdown.
    /// </summary>
    private bool _isUpdatingAgentSessions;

    /// <summary>
    /// When the user switches to an agent view while streaming is active, the root
    /// conversation's <see cref="MessagePanel"/> children are saved here so they can
    /// be restored when the user switches back without re-rendering (which would
    /// cancel the stream).
    /// </summary>
    private List<UIElement>? _savedRootMessageChildren;

    /// <summary>
    /// Per-agent streaming state used by <see cref="RenderAgentStreamToken"/> to
    /// incrementally render sub-agent responses as they arrive via the agent's
    /// <see cref="Agent.TokenCallback"/>. Keyed by agent ID.
    /// </summary>
    private readonly Dictionary<string, AgentStreamingState> _agentStreamingStates = new();

    private IAiChatMode? _activeMode;
    private AiConversation? _activeConversation;
    private readonly List<ModeDropdownPresetItem> _presetDropdownItems = [];
    private bool _isUpdatingModeSelection;
    private ListBox? _mentionPopup;
    private string? _pendingSelectionContext;
    private bool _isUpdatingConversationSelection;
    private const int DefaultOutboundTokenBudget = AiProviderSettings.DefaultContextLength;
    private const int MaxToolCallIterations = 150;

    // ── Raw Mode state ─────────────────────────────────────────────

    /// <summary>
    /// Captured raw JSON request payloads for each API call made during the
    /// current conversation. Each entry holds the full JSON body that was
    /// serialized and sent over the wire to the provider endpoint.
    /// </summary>
    private readonly List<RawRequestPayload> _rawRequestPayloads = [];

    /// <summary>
    /// Represents a single captured API request payload.
    /// </summary>
    private sealed record RawRequestPayload(
        string EndpointUrl,
        string Model,
        string RequestJson,
        string? ResponseContent,
        string? ReasoningContent);

    // ── Stream section visuals ──────────────────────────────────────

    private sealed class StreamSectionVisual(
        Border root,
        Border headerBar,
        TextBlock headerGlyph,
        TextBlock headerText,
        TextBlock? headerStreamContent,
        Border contentBorder,
        StackPanel contentPanel,
        Brush headerBackground,
        Brush contentBackground,
        Brush foreground,
        Brush borderBrush,
        Button? stopButton = null)
    {
        public Border Root { get; } = root;

        public Border HeaderBar { get; } = headerBar;

        public TextBlock HeaderGlyph { get; } = headerGlyph;

        public TextBlock HeaderText { get; } = headerText;

        /// <summary>Streaming content preview column in the header.</summary>
        public TextBlock? HeaderStreamContent { get; } = headerStreamContent;

        public Border ContentBorder { get; } = contentBorder;

        public StackPanel ContentPanel { get; } = contentPanel;

        public Brush HeaderBackground { get; } = headerBackground;

        public Brush ContentBackground { get; } = contentBackground;

        public Brush Foreground { get; set; } = foreground;

        public Brush BorderBrush { get; } = borderBrush;

        public bool IsExpanded { get; set; }

        /// <summary>Optional stop button shown in the header while the tool is executing.</summary>
        public Button? StopButton { get; } = stopButton;
    }

    private sealed class ToolCallSectionVisual(
        StreamSectionVisual section,
        ChunkedTextPresenter argumentsPresenter,
        TextBlock resultBlock,
        string toolName,
        string argumentsJson)
    {
        public StreamSectionVisual Section { get; } = section;

        /// <summary>
        /// Chunked presenter for the tool call arguments. During streaming, only the
        /// current chunk (small, bounded size) is updated. Previous chunks are frozen.
        /// The full content is set once in <see cref="FinalizeToolCallBlock"/>.
        /// </summary>
        public ChunkedTextPresenter ArgumentsPresenter { get; } = argumentsPresenter;

        public TextBlock ResultBlock { get; } = resultBlock;

        /// <summary>The tool name, stored so we can format nicely in <see cref="FinalizeToolCallBlock"/>.</summary>
        public string ToolName { get; } = toolName;

        /// <summary>The final accumulated arguments JSON, stored so we can format nicely in <see cref="FinalizeToolCallBlock"/>.</summary>
        public string ArgumentsJson { get; } = argumentsJson;

        /// <summary>
        /// Per-tool cancellation token source, linked to the global stream CTS.
        /// Set by the tool-execution loop so the stop button can cancel just this
        /// tool's execution without stopping the entire agent loop.
        /// </summary>
        public CancellationTokenSource? ToolCancellation { get; set; }
    }

    /// <summary>
    /// Tracks the streaming state for a sub-agent being viewed in the chat panel.
    /// Holds references to the UI elements being updated as tokens arrive, plus
    /// accumulated text builders so incremental updates can be rendered efficiently
    /// without re-rendering the entire message panel on every token.
    /// </summary>
    private sealed class AgentStreamingState
    {
        public StringBuilder ResponseBuilder { get; } = new();
        public StringBuilder ReasoningBuilder { get; } = new();
        public StackPanel? AssistantContainer;
        public RichTextBox? AssistantBlock;
        public StreamSectionVisual? ThinkingSection;
        public ChunkedTextPresenter? ThinkingPresenter;
        public readonly Dictionary<int, ToolCallSectionVisual> ToolCallBlocks = new();
        public int ReasoningTokenCount;
        public int ContentTokenCount;
        public readonly Stopwatch Stopwatch = Stopwatch.StartNew();
        public bool HasContent;
        public bool HasThinking;
    }

    /// <summary>
    /// Accumulates UI update actions from the background streaming thread and dispatches
    /// them to the UI thread in batches on a fixed interval. This avoids per-token
    /// <c>Dispatcher.InvokeAsync</c> overhead during high-speed streaming.
    ///
    /// Only pure UI updates (text appending, stats, scroll) should be enqueued here.
    /// Structural changes (creating sections, finalizing tool calls) must use
    /// <see cref="DispatchSync"/> so they complete before the next background step.
    /// </summary>
    private sealed class BatchAccumulator : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly TimeSpan _interval;
        private readonly object _lock = new();
        private readonly List<Action> _pending = [];
        private Timer? _timer;
        private int _flushScheduled;

        public BatchAccumulator(Dispatcher dispatcher, TimeSpan interval)
        {
            _dispatcher = dispatcher;
            _interval = interval;
        }

        /// <summary>
        /// Enqueues a UI update to be dispatched in the next batch.
        /// Fast and never blocks — safe to call from background threads.
        /// </summary>
        public void Enqueue(Action action)
        {
            lock (_lock)
            {
                _pending.Add(action);
            }

            EnsureTimer();
        }

        /// <summary>
        /// Dispatches an action synchronously (awaits completion).
        /// Use for structural changes that must complete before background
        /// code continues (e.g. creating a thinking section).
        /// </summary>
        public async Task DispatchSync(Action action)
        {
            await FlushAsync();
            await _dispatcher.InvokeAsync(action);
        }

        /// <summary>
        /// Flushes all pending actions to the UI thread and stops the timer.
        /// Must be called when the streaming phase ends.
        /// </summary>
        public async Task FlushAsync()
        {
            StopTimer();
            await DispatchBatchAsync();
        }

        /// <summary>Disposes the timer. Safe to call after the accumulator is no longer needed.</summary>
        public void Dispose()
        {
            StopTimer();
        }

        private void EnsureTimer()
        {
            if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            {
                _timer = new Timer(
                    _ => { _ = DispatchBatchAsync(); },
                    null,
                    _interval,
                    _interval);
            }
        }

        private void StopTimer()
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            _timer?.Dispose();
            _timer = null;
        }

        private Task DispatchBatchAsync()
        {
            List<Action> batch;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    return Task.CompletedTask;
                }

                batch = [.. _pending];
                _pending.Clear();
            }

            return _dispatcher.InvokeAsync(() =>
            {
                foreach (var action in batch)
                {
                    action();
                }
            }).Task;
        }
    }

    /// <summary>
    /// Manages a collection of TextBlocks inside a panel so that only the current chunk
    /// (bounded to <see cref="_chunkSize"/> characters) is updated on each streaming token.
    /// Previous chunks are frozen — WPF never re-formats them. This avoids the O(n²)
    /// text-layout cost that occurs when setting a very long string on a single TextBlock
    /// on every streaming token.
    /// </summary>
    private sealed class ChunkedTextPresenter
    {
        /// <summary>Maximum characters per chunk. Keeps per-token WPF text-layout work bounded.</summary>
        private const int DefaultChunkSize = 4000;

        private readonly Panel _panel;
        private readonly UIElement? _insertBeforeElement;
        private readonly int _chunkSize;
        private readonly Brush _foreground;
        private readonly FontFamily _fontFamily;
        private readonly double _fontSize;
        private readonly List<TextBlock> _blocks = [];
        private TextBlock? _currentBlock;

        public ChunkedTextPresenter(
            Panel panel,
            Brush foreground,
            FontFamily fontFamily,
            double fontSize,
            UIElement? insertBeforeElement = null,
            int chunkSize = DefaultChunkSize)
        {
            _panel = panel;
            _insertBeforeElement = insertBeforeElement;
            _chunkSize = chunkSize;
            _foreground = foreground;
            _fontFamily = fontFamily;
            _fontSize = fontSize;
        }

        /// <summary>
        /// Appends text to the current chunk. If the current chunk exceeds <see cref="_chunkSize"/>,
        /// a new TextBlock is created and added to the panel. Only the latest TextBlock is ever
        /// updated — previous chunks are frozen.
        /// </summary>
        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            EnsureChunk();
            _currentBlock!.Text += text;
            ApproximateLength += text.Length;
        }

        /// <summary>
        /// Replaces all content with properly-sized chunks. Used after streaming completes
        /// to present the final content efficiently. Only the last chunk may be partial.
        /// </summary>
        public void ReplaceAll(string content)
        {
            Clear();
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            for (int offset = 0; offset < content.Length; offset += _chunkSize)
            {
                int length = Math.Min(_chunkSize, content.Length - offset);
                var block = CreateBlock(content.Substring(offset, length));
                _blocks.Add(block);
                InsertBlock(block);
            }

            ApproximateLength = content.Length;
        }

        /// <summary>
        /// Total characters across all chunks. Used to compute the delta between
        /// the full accumulated argumentsJson and what has been rendered so far.
        /// </summary>
        public int ApproximateLength { get; private set; }

        /// <summary>Removes all managed TextBlocks from the panel.</summary>
        public void Clear()
        {
            foreach (var block in _blocks)
            {
                _panel.Children.Remove(block);
            }

            _blocks.Clear();
            _currentBlock = null;
            ApproximateLength = 0;
        }

        /// <summary>Returns true if no content has been added.</summary>
        public bool IsEmpty => _blocks.Count == 0;

        private void EnsureChunk()
        {
            if (_currentBlock is not null && _currentBlock.Text.Length < _chunkSize)
            {
                return;
            }

            _currentBlock = CreateBlock(string.Empty);
            _blocks.Add(_currentBlock);
            InsertBlock(_currentBlock);
        }

        private TextBlock CreateBlock(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _foreground,
                FontSize = _fontSize,
                FontFamily = _fontFamily,
                Margin = new Thickness(0, 0, 0, 2)
            };
        }

        private void InsertBlock(TextBlock block)
        {
            if (_insertBeforeElement is not null && _insertBeforeElement is UIElement anchor)
            {
                int index = _panel.Children.IndexOf(anchor);
                if (index >= 0)
                {
                    /* Insert before the anchor element (e.g. the result block).
                     * Each new chunk goes right before the anchor, after any
                     * previously inserted chunks. */
                    _panel.Children.Insert(index, block);
                    return;
                }
            }

            _panel.Children.Add(block);
        }
    }

    public AiChatPanel()
    {
        InitializeComponent();
        EnsureActiveConversation();
        RefreshConversationSelector();
        ResetContextWindowBar();
        Loaded += AiChatPanel_Loaded;
        MessageScroller.ScrollChanged += MessageScroller_ScrollChanged;
        MessageScroller.SizeChanged += MessageScroller_SizeChanged;
        SettingsOptionsButton.Checked += SettingsOptionsButton_Checked;
        RawTextCheckBox.Checked += RawTextCheckBox_Changed;
        RawTextCheckBox.Unchecked += RawTextCheckBox_Changed;

        // Initial state: auto expand images is enabled only when auto expand context is off
        AutoExpandImagesCheckBox.IsEnabled = AutoExpandContextCheckBox.IsChecked != true;

        CommandManager.AddPreviewExecutedHandler(InputBox, OnInputBoxPreviewPaste);
        DataObject.AddPastingHandler(InputBox, OnInputBoxPaste);
    }

    /// <summary>
    /// Configures the provider and model to use for chat completions.
    /// </summary>
    internal void Configure(IAiProvider? provider, string? model = null)
    {
        _provider = provider;
        _model = model;
        UpdateSvgToContextCheckBox();
        SyncRootAgentConfig();
    }

    /// <summary>
    /// Synchronizes the root agent with the chat panel's current provider, model,
    /// and mode. Recreates the root agent if any of these have changed. Called
    /// whenever the user switches provider, model, or mode.
    /// </summary>
    private void SyncRootAgentConfig()
    {
        if (_agentOrchestrator is null)
        {
            return;
        }

        Services.Ai.Agents.IAgent? currentRoot = _agentOrchestrator.RootAgent;
        IAiProvider? provider = _provider ?? _providerRegistry?.ActiveProvider;
        IAiChatMode? mode = _activeMode ?? _modeRegistry?.Default;
        string model = _model ?? provider?.AvailableModels.FirstOrDefault() ?? "default";

        if (provider is null || !provider.IsConfigured || mode is null)
        {
            return;
        }

        // Check if the current root agent already matches
        if (currentRoot is not null &&
            ReferenceEquals(currentRoot.Provider, provider) &&
            string.Equals(currentRoot.Model, model, StringComparison.Ordinal) &&
            ReferenceEquals(currentRoot.Mode, mode))
        {
            return;
        }

        // Remove the old root agent (and its descendants)
        if (currentRoot is not null)
        {
            if (_viewingAgentId is not null)
            {
                SwitchToNormalView();
            }

            _agentOrchestrator.RemoveAgent(currentRoot.Id);
        }

        // Create a new root agent with the current config
        string rootId = $"root_{Guid.NewGuid():N}";
        _agentOrchestrator.CreateRootAgent(
            rootId,
            "Main Chat",
            provider,
            model,
            mode);
    }

    /// <summary>
    /// Enables or disables the "Add SVG to context" checkbox based on whether
    /// the current AI provider supports images. When the provider does not support
    /// images, the checkbox is unchecked and greyed out so SVG content is never
    /// attached as vision context regardless of any stale user preference.
    /// </summary>
    private void UpdateSvgToContextCheckBox()
    {
        bool supportsImages = _provider?.SupportsImages ?? false;
        SvgToContextCheckBox.IsEnabled = supportsImages;

        if (!supportsImages)
        {
            SvgToContextCheckBox.IsChecked = false;
        }
    }

    /// <summary>
    /// Selects the best model from the available list, preferring the saved model.
    /// Returns the first available model if no saved model matches or no saved model is set.
    /// Returns null only when the list is empty.
    /// </summary>
    internal static string? SelectModel(IReadOnlyList<string> availableModels, string? preferredModel)
    {
        ArgumentNullException.ThrowIfNull(availableModels);

        if (availableModels.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            foreach (string model in availableModels)
            {
                if (string.Equals(model, preferredModel, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }
        }

        return availableModels[0];
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

    internal void SetCurrentDocumentProvider(Func<AiContextDocumentSnapshot?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _currentDocumentProvider = provider;
    }

    internal void SetOpenDocumentsProvider(Func<IReadOnlyList<AiContextDocumentSnapshot>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _openDocumentsProvider = provider;
    }

    internal void SetBuildOutputProvider(Func<AiBuildOutputSnapshot?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _buildOutputProvider = provider;
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
        RefreshToolsCheckboxPanel();
    }

    internal void SetDebugLogService(AiDebugLogService debugLogService)
    {
        ArgumentNullException.ThrowIfNull(debugLogService);

        _debugLogService = debugLogService;
    }

    /// <summary>
    /// Sets the multi-agent orchestrator. When configured, <c>spawn_agent</c> tool calls
    /// are delegated to the orchestrator instead of being handled inline.
    /// Also subscribes to agent lifecycle events to populate the agent session dropdown.
    /// </summary>
    internal void SetOrchestrator(Services.Ai.Agents.AgentOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Unsubscribe from previous orchestrator if any
        if (_agentOrchestrator is not null)
        {
            _agentOrchestrator.AgentChanged -= Orchestrator_AgentChanged;
        }

        _agentOrchestrator = orchestrator;
        _agentOrchestrator.AgentChanged += Orchestrator_AgentChanged;

        // Populate the dropdown with existing agents
        RefreshAgentSessions();
    }

    /// <summary>
    /// Handles agent lifecycle events from the orchestrator.
    /// Refreshes the agent session dropdown when agents are created or removed.
    /// </summary>
    private void Orchestrator_AgentChanged(object? sender, Services.Ai.Agents.AgentEventArgs e)
    {
        // Dispatch to UI thread since this event may come from a background context
        Dispatcher.BeginInvoke(() => RefreshAgentSessions());
    }

    /// <summary>
    /// Returns the root agent ID if the orchestrator has a root agent, or null.
    /// Used by <see cref="SendMessageAsync"/> to delegate tool execution to the
    /// orchestrator (file-locking, spawn_agent interception).
    /// </summary>
    private string? GetRootAgentId()
    {
        if (_agentOrchestrator?.RootAgent is { } rootAgent)
        {
            return rootAgent.Id;
        }

        return null;
    }

    /// <summary>
    /// Populates the agent session selector dropdown with all current agents
    /// from the orchestrator, showing the root agent and any sub-agents.
    /// Preserves the currently viewed agent selection across refreshes.
    /// </summary>
    private void RefreshAgentSessions()
    {
        if (_agentOrchestrator is null)
        {
            AgentSessionSelector.ItemsSource = null;
            AgentSessionSelector.IsEnabled = false;
            AgentSessionSelector.ToolTip = "Agent orchestrator not initialized";
            return;
        }

        IReadOnlyCollection<Services.Ai.Agents.IAgent> agents = _agentOrchestrator.GetAllAgents();

        if (agents.Count == 0)
        {
            AgentSessionSelector.ItemsSource = null;
            AgentSessionSelector.IsEnabled = false;
            AgentSessionSelector.ToolTip = "No active agents";
            return;
        }

        // Build display items, ordering root first then sub-agents by depth
        List<AgentSessionItem> items = agents
            .OrderBy(a => a.Role)
            .ThenBy(a => a.Id)
            .Select(a => new AgentSessionItem(a))
            .ToList();

        // Remember the currently selected agent ID before rebinding
        string? previouslySelectedId = (AgentSessionSelector.SelectedItem as AgentSessionItem)?.Agent.Id;

        _isUpdatingAgentSessions = true;
        try
        {
            AgentSessionSelector.ItemsSource = items;
            AgentSessionSelector.IsEnabled = true;
            AgentSessionSelector.ToolTip = "Select an agent session";

            // Restore the previously selected agent if it still exists
            if (previouslySelectedId is not null)
            {
                AgentSessionItem? previousItem = items.FirstOrDefault(
                    i => string.Equals(i.Agent.Id, previouslySelectedId, StringComparison.Ordinal));
                if (previousItem is not null)
                {
                    AgentSessionSelector.SelectedItem = previousItem;
                    return;
                }

                // The previously selected agent was removed — switch back to normal view
                if (_viewingAgentId is not null)
                {
                    SwitchToNormalView();
                }
            }

            // Auto-select the root agent if nothing is selected
            if (AgentSessionSelector.SelectedItem is null)
            {
                AgentSessionItem? rootItem = items.FirstOrDefault(i => i.Agent.Role == Services.Ai.Agents.AgentRole.Root);
                if (rootItem is not null)
                {
                    AgentSessionSelector.SelectedItem = rootItem;
                }
            }
        }
        finally
        {
            _isUpdatingAgentSessions = false;
        }
    }

    /// <summary>
    /// Defines the ordered tool group names and their display labels.
    /// Groups appear in the tools dropdown in this order.
    /// </summary>
    private static readonly (string Category, string DisplayLabel)[] ToolGroupDefinitions =
    [
        ("Read Files",     "Read Files"),
        ("Write Files",    "Write Files"),
        ("Dotnet",         "Dotnet"),
        ("Drawing",        "Drawing"),
        ("Presentation",   "Presentation"),
    ];

    /// <summary>
    /// Populates or refreshes the tool checkboxes in the wrench-button popup
    /// based on the current tool registry and active conversation selection.
    /// Tools are grouped by <see cref="IAgentTool.Category"/> with a separator
    /// line and group-level checkbox to enable/disable all tools in the group.
    /// </summary>
    private void RefreshToolsCheckboxPanel()
    {
        ToolsCheckboxPanel.Children.Clear();

        TextBlock header = new()
        {
            Text = "Available Tools",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = FindBrush("AiChatForeground")
        };
        ToolsCheckboxPanel.Children.Add(header);

        if (_toolRegistry is null || !_toolRegistry.HasTools)
        {
            TextBlock noTools = new()
            {
                Text = "(no tools registered)",
                FontSize = 11,
                Foreground = FindBrush("AiChatSecondaryForeground"),
                Margin = new Thickness(0, 0, 0, 2)
            };
            ToolsCheckboxPanel.Children.Add(noTools);
            return;
        }

        AiConversation conversation = EnsureActiveConversation();
        HashSet<string>? enabledTools = conversation.EnabledTools;

        // Group tools by category, preserving definition order for known groups
        // and appending any unknown categories at the end.
        Dictionary<string, List<IAgentTool>> groups = [];
        List<string> groupOrder = [];

        foreach (var (category, _) in ToolGroupDefinitions)
        {
            if (!groups.ContainsKey(category))
            {
                groups[category] = [];
                groupOrder.Add(category);
            }
        }

        foreach (IAgentTool tool in _toolRegistry.Tools)
        {
            string category = tool.Category ?? "General";
            if (!groups.TryGetValue(category, out var list))
            {
                list = [];
                groups[category] = list;
                groupOrder.Add(category);
            }
            list.Add(tool);
        }

        // Sort tools within each group by name
        foreach (var list in groups.Values)
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

            // Separator line between groups
            if (!isFirstGroup)
            {
                Separator separator = new()
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Opacity = 0.4,
                    Foreground = FindBrush("AiChatBorder")
                };
                ToolsCheckboxPanel.Children.Add(separator);
            }
            isFirstGroup = false;

            // Find the display label for this category
            string displayLabel = category;
            foreach (var (cat, label) in ToolGroupDefinitions)
            {
                if (string.Equals(cat, category, StringComparison.Ordinal))
                {
                    displayLabel = label;
                    break;
                }
            }

            // Group-level checkbox
            bool allChecked = groupTools.All(t => enabledTools is null || enabledTools.Contains(t.Name));
            bool anyChecked = groupTools.Any(t => enabledTools is null || enabledTools.Contains(t.Name));
            bool? groupIsChecked = allChecked ? true : anyChecked ? (bool?)null : false;

            CheckBox groupCheckBox = new()
            {
                Content = displayLabel,
                IsChecked = groupIsChecked,
                IsThreeState = true,
                Tag = category,
                Margin = new Thickness(0, 0, 0, 2),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = FindBrush("AiChatForeground"),
                ToolTip = $"Enable or disable all {displayLabel} tools"
            };

            string capturedCategory = category;
            groupCheckBox.Checked += (s, e) =>
            {
                if (s is CheckBox cb && cb.Tag is string cat)
                {
                    ToggleGroupTools(cat, true);
                }
            };
            groupCheckBox.Unchecked += (s, e) =>
            {
                if (s is CheckBox cb && cb.Tag is string cat)
                {
                    ToggleGroupTools(cat, false);
                }
            };
            groupCheckBox.Indeterminate += (s, e) =>
            {
                // When the group transitions from Checked to Indeterminate
                // (three-state cycling: Unchecked → Checked → Indeterminate → Unchecked),
                // the user intended to disable all tools, not keep them enabled.
                // So we disable all tools in the group.
                if (s is CheckBox cb && cb.Tag is string cat)
                {
                    ToggleGroupTools(cat, false);
                }
            };

            ToolsCheckboxPanel.Children.Add(groupCheckBox);

            // Individual tool checkboxes (indented)
            foreach (IAgentTool tool in groupTools)
            {
                bool isChecked = enabledTools is null || enabledTools.Contains(tool.Name);

                CheckBox checkBox = new()
                {
                    Content = tool.Name,
                    IsChecked = isChecked,
                    Tag = tool.Name,
                    Margin = new Thickness(16, 0, 0, 2),
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                    Foreground = FindBrush("AiChatForeground"),
                    ToolTip = tool.Description
                };

                checkBox.Checked += ToolCheckBox_Changed;
                checkBox.Unchecked += ToolCheckBox_Changed;
                ToolsCheckboxPanel.Children.Add(checkBox);
            }
        }
    }

    /// <summary>
    /// Enables or disables all tools in the given category.
    /// </summary>
    private void ToggleGroupTools(string category, bool enable)
    {
        if (_toolRegistry is null)
        {
            return;
        }

        AiConversation conversation = EnsureActiveConversation();
        conversation.EnabledTools ??= new HashSet<string>(StringComparer.Ordinal);

        foreach (IAgentTool tool in _toolRegistry.Tools)
        {
            if (string.Equals(tool.Category, category, StringComparison.Ordinal))
            {
                if (enable)
                {
                    conversation.EnabledTools.Add(tool.Name);
                }
                else
                {
                    conversation.EnabledTools.Remove(tool.Name);
                }
            }
        }

        // Switching tools manually puts the conversation into Custom mode
        SwitchToCustomMode();
        TouchConversation(conversation);
        SavePersistedConversation();

        // Refresh the panel to update individual checkboxes and group state
        RefreshToolsCheckboxPanel();
    }

    /// <summary>
    /// Called when any tool checkbox is checked or unchecked.
    /// Updates the active conversation's EnabledTools set accordingly.
    /// </summary>
    private void ToolCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string toolName)
        {
            return;
        }

        AiConversation conversation = EnsureActiveConversation();
        HashSet<string> enabledTools = conversation.EnabledTools ??= new HashSet<string>(StringComparer.Ordinal);

        if (checkBox.IsChecked == true)
        {
            enabledTools.Add(toolName);
        }
        else
        {
            enabledTools.Remove(toolName);
        }

        // Switching tools manually puts the conversation into Custom mode
        SwitchToCustomMode();

        TouchConversation(conversation);
        SavePersistedConversation();

        // Refresh the checkbox panel so group-level checkboxes reflect
        // the updated partial/checked/unchecked state.
        RefreshToolsCheckboxPanel();
    }

    /// <summary>
    /// Opens the system prompt editor dialog. If the user saves changes,
    /// the conversation switches to Custom mode.
    /// </summary>
    private void SystemPromptButton_Click(object sender, RoutedEventArgs e)
    {
        AiConversation conversation = EnsureActiveConversation();
        AiSystemPromptEditorDialog dialog = new(conversation.SystemPrompt, Window.GetWindow(this));

        if (dialog.ShowDialog() == true)
        {
            conversation.SystemPrompt = dialog.EditedPrompt;
            SwitchToCustomMode();
            TouchConversation(conversation);
            SavePersistedConversation();
            AppendSystemMessage("System prompt updated.");
        }
    }

    /// <summary>
    /// Switches the active conversation to Custom mode in the UI.
    /// </summary>
    private void SwitchToCustomMode()
    {
        if (_activeMode?.Id == "custom")
        {
            return;
        }

        IAiChatMode? customMode = _modeRegistry?.Get("custom");
        if (customMode is null)
        {
            return;
        }

        _activeMode = customMode;
        SelectDropdownItemForMode(customMode);
    }

    /// <summary>
    /// Opens the preset editor window. After the user saves changes, presets
    /// are persisted and the dropdown is refreshed.
    /// </summary>
    private void OpenPresetEditor()
    {
        if (_toolRegistry is null)
        {
            return;
        }

        Window owner = Window.GetWindow(this);
        AiPresetEditorWindow editor = new AiPresetEditorWindow(_toolRegistry, _modeRegistry ?? new AiChatModeRegistry(), _activeMode, owner);

        if (editor.ShowDialog() == true)
        {
            // Presets are saved by the editor; the PresetsSaved event
            // will trigger a dropdown refresh via OnPresetsSaved.
        }
    }

    /// <summary>
    /// Called when the wrench button (SettingsOptionsButton) is checked to open the popup.
    /// Refreshes the tool checkbox list to reflect the current conversation's settings.
    /// </summary>
    private void SettingsOptionsButton_Checked(object sender, RoutedEventArgs e)
    {
        RefreshToolsCheckboxPanel();
    }

    /// <summary>
    /// Returns the tool definitions for the current conversation, using the
    /// per-conversation <see cref="AiConversation.EnabledTools"/> set (which
    /// is initialized from the mode's allowed tools when the mode is switched).
    /// Falls back to the mode's default tool definitions for legacy conversations
    /// that don't have <see cref="AiConversation.EnabledTools"/> set.
    /// </summary>
    private JsonElement GetToolDefinitionsForConversation()
    {
        if (_activeMode is null || !_activeMode.ToolsEnabled || _toolRegistry is null || !_toolRegistry.HasTools)
        {
            return default;
        }

        AiConversation conversation = EnsureActiveConversation();
        HashSet<string>? enabledTools = conversation.EnabledTools;

        // Legacy fallback: conversation hasn't been through a mode switch yet
        if (enabledTools is null)
        {
            return _activeMode.GetToolDefinitions(_toolRegistry);
        }

        if (enabledTools.Count == 0)
        {
            return default;
        }

        return _toolRegistry.SerializeToolDefinitions(enabledTools);
    }

    /// <summary>
    /// Checks whether a tool is allowed in the current conversation, using
    /// the per-conversation <see cref="AiConversation.EnabledTools"/> set.
    /// Falls back to checking the active mode directly for legacy conversations
    /// that don't have <see cref="AiConversation.EnabledTools"/> set.
    /// </summary>
    private bool IsConversationToolAllowed(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (_activeMode is null)
        {
            return false;
        }

        AiConversation conversation = EnsureActiveConversation();
        HashSet<string>? enabledTools = conversation.EnabledTools;

        // Legacy fallback: conversation hasn't been through a mode switch yet
        if (enabledTools is null)
        {
            return _activeMode.AllowedTools is null || _activeMode.AllowedTools.Contains(toolName);
        }

        return enabledTools.Contains(toolName);
    }

    internal void SetExternalContextDirectoryRegistry(ExternalContextDirectoryRegistry externalContextDirectoryRegistry)
    {
        ArgumentNullException.ThrowIfNull(externalContextDirectoryRegistry);
        _externalContextDirectoryRegistry = externalContextDirectoryRegistry;
    }

    /// <summary>
    /// Sets the available chat modes and populates the mode selector dropdown.
    /// The first registered mode is selected by default.
    /// </summary>
    internal void SetModeRegistry(AiChatModeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _modeRegistry = registry;

        AiPresetManager.PresetsSaved += OnPresetsSaved;

        RebuildModeDropdownItems();

        _activeMode = registry.Default;

        if (_activeMode is not null)
        {
            SelectDropdownItemForMode(_activeMode);

            // Apply the mode preset so conversation.EnabledTools reflects
            // the mode's allowed-tool set. Without this, EnabledTools is
            // null and the checkbox panel treats null as "all checked."
            ApplyModePreset(_activeMode);

            // Refresh the tools checkbox panel so the checked/unchecked
            // state correctly reflects the mode's allowed tools on startup.
            RefreshToolsCheckboxPanel();
        }
    }

    /// <summary>
    /// Rebuilds the mode selector dropdown items combining built-in modes and presets.
    /// Built-in modes appear first, followed by a separator, then a "Presets" header,
    /// then individual preset entries, and finally an "Edit presets..." action item.
    /// </summary>
    private void RebuildModeDropdownItems()
    {
        if (_modeRegistry is null)
        {
            return;
        }

        AgentToolRegistry toolRegistry = _toolRegistry ?? new AgentToolRegistry();

        List<object> items = [];

        // 1. Built-in modes
        items.Add(new ModeDropdownHeaderItem { Text = "Built-in" });
        foreach (IAiChatMode mode in _modeRegistry.Modes)
        {
            items.Add(new ModeDropdownModeItem { Mode = mode });
        }

        // 2. Separator
        items.Add(new ModeDropdownSeparatorItem());

        // 3. Presets section
        items.Add(new ModeDropdownHeaderItem { Text = "Presets" });

        _presetDropdownItems.Clear();
        List<Models.AiPreset> presets = AiPresetManager.Load();
        foreach (Models.AiPreset preset in presets)
        {
            PresetMode presetMode = new PresetMode(preset, toolRegistry);
            var dropdownItem = new ModeDropdownPresetItem
            {
                Preset = preset,
                Mode = presetMode
            };
            _presetDropdownItems.Add(dropdownItem);
            items.Add(dropdownItem);
        }

        // 4. Edit presets action item (when clicked, opens the preset editor)
        items.Add(new ModeDropdownActionItem
        {
            Text = "✏️ Edit presets...",
            Action = () =>
            {
                OpenPresetEditor();
                // Restore the previously active selection after editing
                if (_activeMode is not null)
                {
                    SelectDropdownItemForMode(_activeMode);
                }
            }
        });

        _isUpdatingModeSelection = true;
        try
        {
            ModeSelector.ItemsSource = items;
        }
        finally
        {
            _isUpdatingModeSelection = false;
        }
    }

    /// <summary>
    /// Refreshes the preset dropdown items (called after presets are saved).
    /// Preserves the current selection if the active mode still exists.
    /// </summary>
    private void RefreshPresetDropdownItems()
    {
        IAiChatMode? previouslyActiveMode = _activeMode;
        string? previousPresetId = null;

        if (previouslyActiveMode?.Id.StartsWith("preset:", StringComparison.Ordinal) == true)
        {
            previousPresetId = previouslyActiveMode.Id;
        }

        RebuildModeDropdownItems();

        // Try to restore the previous selection
        if (previousPresetId is not null)
        {
            // Try to find the same preset
            foreach (var presetItem in _presetDropdownItems)
            {
                if (string.Equals(presetItem.Mode.Id, previousPresetId, StringComparison.Ordinal))
                {
                    SelectDropdownItemForMode(presetItem.Mode);
                    return;
                }
            }

            // Preset was deleted — fall back to first preset or custom mode
            if (_presetDropdownItems.Count > 0)
            {
                SelectDropdownItemForMode(_presetDropdownItems[0].Mode);
            }
            else
            {
                IAiChatMode? customMode = _modeRegistry?.Get("custom");
                if (customMode is not null)
                {
                    _activeMode = customMode;
                    SelectDropdownItemForMode(customMode);
                }
            }
        }
        else if (previouslyActiveMode is not null)
        {
            SelectDropdownItemForMode(previouslyActiveMode);
        }
    }

    /// <summary>
    /// Finds and selects the dropdown item corresponding to the given mode.
    /// </summary>
    private void SelectDropdownItemForMode(IAiChatMode mode)
    {
        _isUpdatingModeSelection = true;
        try
        {
            foreach (object item in ModeSelector.Items)
            {
                if (item is ModeDropdownModeItem modeItem && ReferenceEquals(modeItem.Mode, mode))
                {
                    ModeSelector.SelectedItem = item;
                    _activeMode = mode;
                    return;
                }

                if (item is ModeDropdownPresetItem presetItem && string.Equals(presetItem.Mode.Id, mode.Id, StringComparison.Ordinal))
                {
                    ModeSelector.SelectedItem = item;
                    _activeMode = presetItem.Mode;
                    return;
                }
            }

            // Fallback: try by id
            foreach (object item in ModeSelector.Items)
            {
                if (item is ModeDropdownModeItem modeItem && string.Equals(modeItem.Mode.Id, mode.Id, StringComparison.Ordinal))
                {
                    ModeSelector.SelectedItem = item;
                    _activeMode = modeItem.Mode;
                    return;
                }
            }
        }
        finally
        {
            _isUpdatingModeSelection = false;
        }
    }

    private void OnPresetsSaved(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(() => RefreshPresetDropdownItems());
        }
    }

    /// <summary>
    /// Programmatically switches the active conversation to the mode with the given <paramref name="modeId"/>.
    /// If the mode is found and is different from the current mode, it is applied as a preset.
    /// Supports both built-in mode IDs (e.g. "agent") and preset IDs (e.g. "preset:&lt;guid&gt;").
    /// </summary>
    internal void SwitchToMode(string modeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);

        if (_modeRegistry is null)
        {
            return;
        }

        if (string.Equals(_activeMode?.Id, modeId, StringComparison.Ordinal))
        {
            return;
        }

        IAiChatMode? mode = null;

        // Check if this is a preset mode id
        if (modeId.StartsWith("preset:", StringComparison.Ordinal))
        {
            foreach (ModeDropdownPresetItem presetItem in _presetDropdownItems)
            {
                if (string.Equals(presetItem.Mode.Id, modeId, StringComparison.Ordinal))
                {
                    mode = presetItem.Mode;
                    break;
                }
            }
        }
        else
        {
            mode = _modeRegistry.Get(modeId);
        }

        if (mode is null)
        {
            return;
        }

        _activeMode = mode;
        SelectDropdownItemForMode(mode);

        if (mode.Id != "custom")
        {
            ApplyModePreset(mode);
        }

        AppendSystemMessage($"Switched to {mode.DisplayName} mode.");
        RefreshToolsCheckboxPanel();
        RefreshContextWindowDisplay();
        SyncRootAgentConfig();
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
        // Clear any sub-agents from a previous session
        RemoveAllSubAgents();
        SwitchToNormalView();

        string? key = _projectConversationKeyProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(key))
        {
            EnsureActiveConversation();
            RefreshConversationSelector();
            RenderActiveConversation();
            return;
        }

        try
        {
            AiConversationState loadedState = AiConversationStore.LoadState(key);
            _conversationState.Conversations.Clear();
            _conversationState.Conversations.AddRange(loadedState.Conversations);
            _conversationState.ActiveConversationId = loadedState.ActiveConversationId;
            AiConversation activeConversation = EnsureActiveConversation();
            RefreshConversationSelector();
            RenderActiveConversation();

            if (loadedState.Conversations.Count > 0)
            {
                AppendSystemMessage($"Loaded {loadedState.Conversations.Count} saved conversation(s) for this project.");
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
    }

    private void SavePersistedConversation()
    {
        string? key = _projectConversationKeyProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        try
        {
            AiConversation activeConversation = EnsureActiveConversation();
            TouchConversation(activeConversation);
            _conversationState.ActiveConversationId = activeConversation.Id;
            AiConversationStore.SaveState(key, _conversationState);
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
    /// Stores inline image borders keyed by their reference so they can be removed
    /// when the user clicks the remove button on either the inline image or the tag.
    /// </summary>
    private readonly Dictionary<AiChatReference, Border> _inlineImageBorders = [];

    /// <summary>
    /// Stores inline context section visuals keyed by their reference so they can be removed
    /// when the user clicks the remove button on the reference tag.
    /// </summary>
    private readonly Dictionary<AiChatReference, StreamSectionVisual> _inlineContextSections = [];

    /// <summary>
    /// Adds a file reference to the next message context.
    /// </summary>
    internal void AddFileReference(string filePath)
    {
        AddReference(AiContextReferenceFactory.CreateFileReference(filePath));
    }

    internal void AddReference(AiChatReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        AiConversation conversation = EnsureActiveConversation();

        if (conversation.References.Any(existingReference => AreSameReference(existingReference, reference)))
        {
            return;
        }

        conversation.References.Add(reference);
        TouchConversation(conversation);

        AppendContextSection(reference);

        RenderReferenceTags();
        SavePersistedConversation();
    }

    private void RemoveReference(AiChatReference reference)
    {
        AiConversation conversation = EnsureActiveConversation();
        conversation.References.Remove(reference);
        TouchConversation(conversation);

        // Remove the inline image border if one exists for this reference
        if (_inlineImageBorders.TryGetValue(reference, out Border? inlineBorder))
        {
            MessagePanel.Children.Remove(inlineBorder);
            _inlineImageBorders.Remove(reference);
        }

        // Remove the inline context section if one exists for this reference
        if (_inlineContextSections.TryGetValue(reference, out StreamSectionVisual? contextSection))
        {
            RemoveInlineSection(contextSection);
            _inlineContextSections.Remove(reference);
        }

        RenderReferenceTags();
        SavePersistedConversation();
    }

    private void RenderReferenceTags()
    {
        // Reference tags have been replaced by inline context sections in the chat.
        // The Add/Paste buttons remain in the reference bar for adding new context.
    }

    private static bool AreSameReference(AiChatReference left, AiChatReference right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Kind != right.Kind)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.FullPath) || !string.IsNullOrWhiteSpace(right.FullPath))
        {
            return string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the context injection text from all attached references.
    /// </summary>
    internal static string BuildReferenceContext(IReadOnlyList<AiChatReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (references.Count == 0)
        {
            return string.Empty;
        }

        System.Text.StringBuilder sb = new();
        sb.AppendLine("The user has attached the following context for this conversation:");
        sb.AppendLine();

        foreach (AiChatReference reference in references)
        {
            sb.AppendLine(reference.ToContextString());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static string BuildPendingPromptContext(IReadOnlyList<AiChatReference> references, string? selectionContext)
    {
        ArgumentNullException.ThrowIfNull(references);

        List<string> sections = [];
        string referenceContext = BuildReferenceContext(references).TrimEnd();

        if (!string.IsNullOrWhiteSpace(referenceContext))
        {
            sections.Add(referenceContext);
        }

        if (!string.IsNullOrWhiteSpace(selectionContext))
        {
            sections.Add(selectionContext.TrimEnd());
        }

        return string.Join("\n\n", sections);
    }

    internal static List<AiChatMessage> BuildRequestConversationHistory(IReadOnlyList<AiChatMessage> persistedConversationHistory, string outboundUserContent)
    {
        ArgumentNullException.ThrowIfNull(persistedConversationHistory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundUserContent);

        List<AiChatMessage> requestConversationHistory = persistedConversationHistory
            .Select(CloneConversationMessage)
            .ToList();

        if (requestConversationHistory.Count == 0 || requestConversationHistory[^1].Role != AiChatRole.User)
        {
            throw new InvalidOperationException("The request conversation history must end with a user message.");
        }

        requestConversationHistory[^1] = requestConversationHistory[^1] with { Content = outboundUserContent };
        return requestConversationHistory;
    }

    private static AiChatMessage CloneConversationMessage(AiChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message with
        {
            ToolCalls = message.ToolCalls is null ? null : [.. message.ToolCalls]
        };
    }

    internal static string CreateConversationTitle(string messageText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageText);

        string singleLineTitle = string.Join(
            " ",
            messageText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(singleLineTitle))
        {
            return "New conversation";
        }

        return singleLineTitle.Length <= 48
            ? singleLineTitle
            : singleLineTitle[..47] + "…";
    }

    private AiConversation EnsureActiveConversation()
    {
        if (_activeConversation is not null)
        {
            return _activeConversation;
        }

        if (_conversationState.Conversations.Count == 0)
        {
            AiConversation conversation = CreateConversation();
            _conversationState.Conversations.Add(conversation);
            _conversationState.ActiveConversationId = conversation.Id;
            _activeConversation = conversation;
            return conversation;
        }

        AiConversation? existingConversation = _conversationState.Conversations
            .FirstOrDefault(conversation => string.Equals(conversation.Id, _conversationState.ActiveConversationId, StringComparison.Ordinal));

        _activeConversation = existingConversation ?? _conversationState.Conversations[0];
        _conversationState.ActiveConversationId = _activeConversation.Id;
        return _activeConversation;
    }

    private AiConversation CreateConversation()
    {
        int conversationNumber = _conversationState.Conversations.Count + 1;
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        return new AiConversation
        {
            Title = $"Conversation {conversationNumber}",
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp
        };
    }

    /// <summary>
    /// Resolves auto-context rules from settings and adds matching files to the conversation.
    /// Bare filenames (e.g. "agents.md") search recursively from the project root.
    /// Relative paths (e.g. "docs/notes.md") resolve to an exact location.
    /// </summary>
    private void ApplyAutoContextRules(AiConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        string? projectKey = _projectConversationKeyProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(projectKey))
        {
            return;
        }

        string projectRoot;
        try
        {
            projectRoot = Services.Ai.Tools.AgentToolPathResolver.GetProjectRootDirectory(() => projectKey);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!Directory.Exists(projectRoot))
        {
            return;
        }

        IReadOnlyList<string> rules = Services.Ai.AutoContextSettingsManager.Load();
        if (rules.Count == 0)
        {
            return;
        }

        HashSet<string> skippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea",
            "bin", "obj",
            "node_modules", ".npm",
            "packages", ".nuget",
            "__pycache__", ".venv", "venv",
        };

        foreach (string rule in rules)
        {
            string trimmedRule = rule.Trim();
            if (string.IsNullOrWhiteSpace(trimmedRule))
            {
                continue;
            }

            bool containsSeparator = trimmedRule.Contains(Path.DirectorySeparatorChar)
                || trimmedRule.Contains(Path.AltDirectorySeparatorChar);

            if (containsSeparator || Path.IsPathRooted(trimmedRule))
            {
                // Relative or absolute path — resolve exact location
                string fullPath = Path.IsPathRooted(trimmedRule)
                    ? Path.GetFullPath(trimmedRule)
                    : Path.GetFullPath(Path.Combine(projectRoot, trimmedRule));

                if (File.Exists(fullPath))
                {
                    AddFileReferenceToConversation(conversation, fullPath);
                }
            }
            else
            {
                // Bare filename — search recursively from project root
                try
                {
                    foreach (string path in Directory.EnumerateFiles(projectRoot, trimmedRule, SearchOption.AllDirectories))
                    {
                        if (IsInSkippedDirectory(path, skippedDirectories))
                        {
                            continue;
                        }

                        AddFileReferenceToConversation(conversation, path);
                    }
                }
                catch (IOException)
                {
                    // Skip directories we can't enumerate
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we can't access
                }
            }
        }
    }

    private static void AddFileReferenceToConversation(AiConversation conversation, string filePath)
    {
        if (conversation.References.Any(r => string.Equals(r.FullPath, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            AiChatReference reference = AiContextReferenceFactory.CreateFileReference(filePath);
            conversation.References.Add(reference);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsInSkippedDirectory(string path, HashSet<string> skippedDirectories)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string part in parts)
        {
            if (skippedDirectories.Contains(part))
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshConversationSelector()
    {
        AiConversation activeConversation = EnsureActiveConversation();
        _isUpdatingConversationSelection = true;

        try
        {
            ConversationSelector.ItemsSource = null;
            ConversationSelector.ItemsSource = _conversationState.Conversations;
            ConversationSelector.SelectedItem = activeConversation;
        }
        finally
        {
            _isUpdatingConversationSelection = false;
        }
    }

    private void TouchConversation(AiConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    private void EnsureConversationTitle(AiConversation conversation, string userText)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        if (conversation.Messages.Count > 0 || !conversation.References.Any())
        {
            if (!string.Equals(conversation.Title, "New conversation", StringComparison.OrdinalIgnoreCase) &&
                !conversation.Title.StartsWith("Conversation ", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (conversation.Messages.Count == 0)
        {
            conversation.Title = CreateConversationTitle(userText);
        }
    }

    private static string BuildToolResultHeader(string toolName, string toolCallId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);

        return $"{toolName} ({toolCallId})";
    }

    private static bool TryParseToolResult(string content, out bool success, out string resultText)
    {
        ArgumentNullException.ThrowIfNull(content);

        const string errorPrefix = "Error: ";
        if (content.StartsWith(errorPrefix, StringComparison.Ordinal))
        {
            success = false;
            resultText = content[errorPrefix.Length..];
            return true;
        }

        success = true;
        resultText = content;
        return true;
    }

    private void ClearConversationReferences()
    {
        AiConversation conversation = EnsureActiveConversation();
        conversation.References.Clear();
        _inlineContextSections.Clear();
        _inlineImageBorders.Clear();
        RenderReferenceTags();
        SavePersistedConversation();
    }

    private void AddMessageToHistories(List<AiChatMessage> requestConversationHistory, AiChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(requestConversationHistory);
        ArgumentNullException.ThrowIfNull(message);

        AiConversation conversation = EnsureActiveConversation();
        conversation.Messages.Add(message);
        TouchConversation(conversation);
        requestConversationHistory.Add(CloneConversationMessage(message));
    }

    private void AddReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ProjectItem> projectItems = _projectItemsProvider?.Invoke() ?? [];
        AiContextDocumentSnapshot? currentDocument = _currentDocumentProvider?.Invoke();
        IReadOnlyList<AiContextDocumentSnapshot> openDocuments = _openDocumentsProvider?.Invoke() ?? [];
        AiBuildOutputSnapshot? buildOutput = _buildOutputProvider?.Invoke();

        AiReferencePickerDialog dialog = new(projectItems, currentDocument, openDocuments, buildOutput)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (AiChatReference reference in dialog.SelectedReferences)
            {
                AddReference(reference);
            }
        }
    }

    /// <summary>
    /// Reads an image from the clipboard (screenshot, copied picture, or copied image file)
    /// and adds it as an <see cref="AiReferenceKind.Image"/> reference so it is automatically
    /// attached as vision context on the next message (when the provider supports images).
    /// </summary>
    private void PasteImageButton_Click(object sender, RoutedEventArgs e)
    {
        PasteImageFromClipboard();
    }

    /// <summary>
    /// Shared clipboard-image paste logic used by both the inline input and the expanded
    /// input window. Reads an image from the clipboard and attaches it as vision context.
    /// </summary>
    private void PasteImageFromClipboard()
    {
        try
        {
            if (TryGetImageBytesFromClipboard(out byte[] imageBytes, out string extension))
            {
                string fileName = SavePastedImage(imageBytes, extension);
                AddReference(AiContextReferenceFactory.CreateImageReference(fileName));
                AppendSystemMessage("Image pasted — attached as vision context for the next message.");
                return;
            }

            if (TryGetImageFilePathFromClipboard(out string imageFilePath))
            {
                AddReference(AiContextReferenceFactory.CreateImageReference(imageFilePath));
                AppendSystemMessage("Image pasted — attached as vision context for the next message.");
                return;
            }

            AppendSystemMessage("No image found on the clipboard.");
        }
        catch (IOException ex)
        {
            AppendSystemMessage($"Could not paste image: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendSystemMessage($"Could not paste image: {ex.Message}");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            AppendSystemMessage($"Could not paste image: {ex.Message}");
        }
    }

    /// <summary>
    /// The directory under <see cref="PortablePathProvider.BaseDirectory"/> where
    /// clipboard-pasted images are persisted. Content is addressed by a hash of the
    /// image bytes so re-pasting the same image reuses the same file (and the same
    /// reference, which is de-duplicated by path).
    /// </summary>
    internal static string PastedImagesDirectory =>
        Path.Combine(PortablePathProvider.BaseDirectory, "ai-pasted-images");

    /// <summary>
    /// Saves raw image bytes to the pasted-images directory using a file name derived
    /// from a SHA-256 hash of the bytes (so identical images share a path). Returns the
    /// absolute file path of the saved image. Existing files with the same hash are reused.
    /// </summary>
    internal static string SavePastedImage(byte[] imageBytes, string extension)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("The image data is empty.", nameof(imageBytes));
        }

        string normalizedExtension = string.IsNullOrWhiteSpace(extension)
            ? ".png"
            : (extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension).ToLowerInvariant();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes)).ToLowerInvariant();
        string directoryPath = PastedImagesDirectory;
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, $"{hash}{normalizedExtension}");

        if (!File.Exists(filePath))
        {
            File.WriteAllBytes(filePath, imageBytes);
        }

        return filePath;
    }

    /// <summary>
    /// Attempts to read image bytes from the clipboard. Prefers lossless PNG bytes placed
    /// on the clipboard by most screenshot and browser tools; falls back to encoding a
    /// <see cref="BitmapSource"/> (e.g. a copied picture) as PNG. Returns false when the
    /// clipboard holds no recognized image data.
    /// </summary>
    internal static bool TryGetImageBytesFromClipboard(out byte[] imageBytes, out string extension)
    {
        imageBytes = [];
        extension = ".png";

        IDataObject? dataObject = GetClipboardData();
        if (dataObject is null)
        {
            return false;
        }

        return TryParseImageBytesFromDataObject(dataObject, out imageBytes, out extension);
    }

    /// <summary>
    /// Parses image bytes from an <see cref="IDataObject"/> (e.g. the clipboard data object).
    /// Prefers lossless PNG bytes placed on the clipboard by most screenshot and browser
    /// tools; falls back to encoding a <see cref="BitmapSource"/> (e.g. a copied picture)
    /// as PNG. Returns false when the data object holds no recognized image data.
    /// </summary>
    internal static bool TryParseImageBytesFromDataObject(IDataObject dataObject, out byte[] imageBytes, out string extension)
    {
        ArgumentNullException.ThrowIfNull(dataObject);

        imageBytes = [];
        extension = ".png";

        // Most screenshot/snipping tools place PNG bytes directly on the clipboard.
        byte[]? pngBytes = dataObject.GetData("PNG") as byte[];
        if (pngBytes is { Length: > 0 })
        {
            imageBytes = pngBytes;
            extension = ".png";
            return true;
        }

        byte[]? jpegBytes = dataObject.GetData("JPG") as byte[] ?? dataObject.GetData("JPEG") as byte[];
        if (jpegBytes is { Length: > 0 })
        {
            imageBytes = jpegBytes;
            extension = ".jpg";
            return true;
        }

        byte[]? gifBytes = dataObject.GetData("GIF") as byte[];
        if (gifBytes is { Length: > 0 })
        {
            imageBytes = gifBytes;
            extension = ".gif";
            return true;
        }

        // Fallback: any BitmapSource we can coerce into a real image.
        BitmapSource? bitmap = GetClipboardBitmap(dataObject);
        if (bitmap is not null)
        {
            imageBytes = EncodeBitmapAsPng(bitmap);
            extension = ".png";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to read an image file path from the clipboard (file-drag/copy of an image).
    /// Returns false when no image file path is present.
    /// </summary>
    internal static bool TryGetImageFilePathFromClipboard(out string imageFilePath)
    {
        imageFilePath = string.Empty;

        IDataObject? dataObject = GetClipboardData();
        if (dataObject is null || !dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        return TryParseImageFilePathFromDataObject(dataObject, out imageFilePath);
    }

    /// <summary>
    /// Parses an image file path from an <see cref="IDataObject"/> (e.g. a clipboard
    /// file drop). Returns false when no image file path is present.
    /// </summary>
    internal static bool TryParseImageFilePathFromDataObject(IDataObject dataObject, out string imageFilePath)
    {
        ArgumentNullException.ThrowIfNull(dataObject);

        imageFilePath = string.Empty;

        if (!dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        string[]? droppedFilePaths = dataObject.GetData(DataFormats.FileDrop) as string[];
        if (droppedFilePaths is null || droppedFilePaths.Length == 0)
        {
            return false;
        }

        string firstPath = droppedFilePaths[0];
        if (!File.Exists(firstPath))
        {
            return false;
        }

        string extension = Path.GetExtension(firstPath);
        if (!AiContextReferenceFactory.IsImageExtension(extension))
        {
            return false;
        }

        imageFilePath = firstPath;
        return true;
    }

    /// <summary>
    /// Reads the current clipboard data object, swallowing the transient
    /// <see cref="COMException"/> that occurs when another process holds the clipboard open.
    /// </summary>
    private static IDataObject? GetClipboardData()
    {
        try
        {
            return Clipboard.GetDataObject();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract a <see cref="BitmapSource"/> image from an <see cref="IDataObject"/>,
    /// coercing bitmap/DIB formats into a usable image source.
    /// </summary>
    private static BitmapSource? GetClipboardBitmap(IDataObject dataObject)
    {
        try
        {
            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                object? bitmapData = dataObject.GetData(DataFormats.Bitmap);
                if (bitmapData is BitmapSource bitmapSource)
                {
                    return bitmapSource;
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return null;
    }

    /// <summary>
    /// Encodes a <see cref="BitmapSource"/> as PNG bytes. Returns an empty array if the
    /// source cannot be encoded (e.g. it is frozen or has no valid dimensions).
    /// </summary>
    private static byte[] EncodeBitmapAsPng(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        try
        {
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using MemoryStream stream = new();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (ArgumentException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    private void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        // Remove all sub-agents from the previous conversation
        RemoveAllSubAgents();
        SwitchToNormalView();

        AiConversation conversation = CreateConversation();
        _conversationState.Conversations.Insert(0, conversation);
        _conversationState.ActiveConversationId = conversation.Id;
        _activeConversation = conversation;
        ApplyAutoContextRules(conversation);
        RefreshConversationSelector();
        RenderActiveConversation();
        SavePersistedConversation();
        RefreshToolsCheckboxPanel();
    }

    /// <summary>
    /// Handles selection changes on the agent session selector dropdown.
    /// Switches the chat panel to display the selected agent's conversation.
    /// </summary>
    private void AgentSessionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Don't react to programmatic selection changes during dropdown refreshes
        if (_isUpdatingAgentSessions)
        {
            return;
        }

        if (AgentSessionSelector.SelectedItem is not AgentSessionItem selectedItem)
        {
            return;
        }

        Services.Ai.Agents.IAgent agent = selectedItem.Agent;

        // If selecting the root agent, restore the normal conversation view
        if (agent.Role == Services.Ai.Agents.AgentRole.Root)
        {
            SwitchToNormalView();
            return;
        }

        // Switching to a sub-agent — render its conversation
        SwitchToAgentView(agent);
    }

    /// <summary>
    /// Restores the normal conversation view (the root agent / AiConversation).
    /// Re-enables the input panel. If streaming is still active (e.g. a sub-agent
    /// is running), the root conversation's saved UI elements are restored rather
    /// than re-rendering (which would cancel the stream).
    /// </summary>
    private void SwitchToNormalView()
    {
        _viewingAgentId = null;

        // Restore the input area
        SetInputEnabled(true);

        // Restore normal Send button behavior
        SendButton.Content = _isStreaming ? "⏹ Stop" : "Send";
        SendButton.Click -= BackToChatButton_Click;
        SendButton.Click -= StopButton_Click;
        SendButton.Click -= SendButton_Click;

        if (_isStreaming)
        {
            SendButton.Click += StopButton_Click;
        }
        else
        {
            SendButton.Click += SendButton_Click;
        }

        // Restore the root conversation view
        if (_isStreaming && _savedRootMessageChildren is not null)
        {
            // Restore the root conversation's UI elements that were saved
            // when the user switched to the agent view.
            _streamSections.Clear();
            _inlineImageBorders.Clear();
            _inlineContextSections.Clear();
            PinnedSectionPanel.Children.Clear();
            PinnedSectionPanel.Visibility = Visibility.Collapsed;
            MessagePanel.Children.Clear();

            foreach (UIElement child in _savedRootMessageChildren)
            {
                MessagePanel.Children.Add(child);
            }

            _savedRootMessageChildren = null;
        }
        else if (!_isStreaming)
        {
            // Streaming not active — safe to fully re-render the conversation
            _savedRootMessageChildren = null;
            RenderActiveConversation();
        }
        else
        {
            // Streaming is active but saved children are null. This can happen
            // if the user switched to an agent view when streaming was not active,
            // or if the saved children were already consumed by a prior call.
            // Do NOT call RenderActiveConversation() here — it would call
            // CancelStreaming() and kill the in-progress root conversation.
            // The streaming loop's finally block will handle the UI update
            // when it completes (if _viewingAgentId is null at that point).
            _savedRootMessageChildren = null;
        }

        // Re-select the root agent in the dropdown if needed.
        // Wrap in _isUpdatingAgentSessions to suppress the SelectionChanged
        // handler — without this, setting SelectedItem fires
        // AgentSessionSelector_SelectionChanged, which calls SwitchToNormalView()
        // a second time. In that second call _savedRootMessageChildren is already
        // null, so it falls through to RenderActiveConversation() → CancelStreaming(),
        // killing the root conversation mid-stream.
        if (_agentOrchestrator?.RootAgent is { } rootAgent)
        {
            _isUpdatingAgentSessions = true;
            try
            {
                foreach (object item in AgentSessionSelector.Items)
                {
                    if (item is AgentSessionItem agentItem &&
                        string.Equals(agentItem.Agent.Id, rootAgent.Id, StringComparison.Ordinal))
                    {
                        AgentSessionSelector.SelectedItem = item;
                        break;
                    }
                }
            }
            finally
            {
                _isUpdatingAgentSessions = false;
            }
        }
    }

    /// <summary>
    /// Switches the chat panel to display the specified agent's conversation.
    /// Disables the input panel and shows a "◀ Back" button to return to the
    /// normal conversation view.
    /// </summary>
    private void SwitchToAgentView(Services.Ai.Agents.IAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        _viewingAgentId = agent.Id;

        // Disable the input area — the user cannot send messages to a sub-agent
        SetInputEnabled(false);

        // Change Send button to "◀ Back"
        SendButton.Content = "◀ Back";
        SendButton.Click -= SendButton_Click;
        SendButton.Click -= StopButton_Click;
        SendButton.Click += BackToChatButton_Click;

        // Do NOT cancel streaming — the sub-agent (or root loop) is still running
        // on a background thread and should continue uninterrupted.

        // If streaming is active, save the root conversation's UI children before
        // clearing the panel so they can be restored when the user switches back.
        if (_isStreaming)
        {
            _savedRootMessageChildren = MessagePanel.Children.Cast<UIElement>().ToList();
        }
        else
        {
            _savedRootMessageChildren = null;
        }

        // Clear and re-render using the agent's current messages.
        // Live updates will be dispatched via the agent's IterationCallback
        // (set up in ExecuteSpawnAgentInternalAsync).
        RefreshAgentViewMessages(agent);
    }

    /// <summary>
    /// Clears the message panel and re-renders the agent's current messages.
    /// Unlike <see cref="RenderAgentMessages"/>, this also updates the stats bar.
    /// Called both on initial switch and from the agent's iteration callback
    /// for live progress updates.
    /// </summary>
    private void RefreshAgentViewMessages(Services.Ai.Agents.IAgent agent)
    {
        _streamSections.Clear();
        _inlineImageBorders.Clear();
        _inlineContextSections.Clear();
        MessagePanel.Children.Clear();
        PinnedSectionPanel.Children.Clear();
        PinnedSectionPanel.Visibility = Visibility.Collapsed;

        RenderAgentMessages(agent.Messages);

        // Update stats bar for the agent
        int messageCount = agent.Messages.Count;
        int toolCallCount = agent.Messages.Count(m => m.Role == AiChatRole.Tool);
        StatsBar.Text = $"{messageCount} msgs · {toolCallCount} tool results";
        ContextWindowBar.Text = $"agent: {agent.DisplayName}  •  model: {agent.Model}";
    }

    /// <summary>
    /// Renders a list of AI chat messages into the message panel.
    /// This is a standalone version of <see cref="RebuildNormalConversation"/>
    /// that operates directly on a message list instead of an <see cref="AiConversation"/>.
    /// </summary>
    private void RenderAgentMessages(IReadOnlyList<AiChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Dictionary<string, ToolCallSectionVisual> toolCallBlocks = new(StringComparer.Ordinal);
        StackPanel? lastAssistantContainer = null;
        RichTextBox? lastAssistantBlock = null;

        foreach (AiChatMessage message in messages)
        {
            switch (message.Role)
            {
                case AiChatRole.System:
                    AppendSystemMessage(message.Content);
                    lastAssistantContainer = null;
                    lastAssistantBlock = null;
                    break;
                case AiChatRole.User:
                    AppendUserMessage(message.Content);
                    lastAssistantContainer = null;
                    lastAssistantBlock = null;
                    break;
                case AiChatRole.Assistant:
                    (lastAssistantContainer, lastAssistantBlock) = CreateAssistantMessageBlock();

                    if (!string.IsNullOrWhiteSpace(message.ThinkingContent))
                    {
                        (StreamSectionVisual thinkingSection, ChunkedTextPresenter thinkingPresenter) = CreateThinkingSection(
                            lastAssistantContainer, lastAssistantBlock);
                        thinkingPresenter.ReplaceAll(FormatDisplayedAssistantContent(
                            message.ThinkingContent, ShouldRemoveVerticalWhitespace()));
                        SetInlineSectionHeader(thinkingSection, "Thought");
                    }

                    if (message.ToolCalls is not null)
                    {
                        foreach (AiToolCallRequest toolCall in message.ToolCalls)
                        {
                            ToolCallSectionVisual block = CreateToolCallBlock(
                                toolCall.FunctionName, toolCall.ArgumentsJson,
                                lastAssistantContainer, lastAssistantBlock);
                            toolCallBlocks[toolCall.Id] = block;
                        }
                    }

                    RenderAssistantContent(lastAssistantBlock, message.Content);
                    break;
                case AiChatRole.Tool:
                    if (message.ToolCallId is not null &&
                        toolCallBlocks.TryGetValue(message.ToolCallId, out ToolCallSectionVisual? toolCallBlock))
                    {
                        TryParseToolResult(message.Content, out bool success, out string resultText);
                        ToolCallResult toolCallResult = success
                            ? ToolCallResult.Ok(resultText)
                            : ToolCallResult.Fail(resultText);
                        FinalizeToolCallBlock(toolCallBlock, toolCallResult);
                    }
                    else
                    {
                        AppendSystemMessage($"Tool result: {message.Content}");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Renders a streaming token from a sub-agent's <see cref="Agent.RunAsync"/> loop
    /// to the message panel, enabling real-time text streaming when viewing a sub-agent.
    ///
    /// This method is invoked via the agent's <see cref="Agent.TokenCallback"/> (set in
    /// <see cref="ExecuteSpawnAgentInternalAsync"/>). The callback is dispatched to the
    /// UI thread via <see cref="Dispatcher.BeginInvoke"/>, so this method always runs
    /// on the UI thread. It accumulates token text in an <see cref="AgentStreamingState"/>
    /// and renders it incrementally — mirroring the streaming behavior of the main
    /// <see cref="SendMessageAsync"/> loop.
    ///
    /// The streaming state is cleared by <see cref="RefreshAgentViewMessages"/> (called
    /// from the <see cref="Agent.IterationCallback"/> at the start of each iteration,
    /// from the final refresh after <see cref cref="Agent.RunAsync"/> completes, and from
    /// <see cref="SwitchToAgentView"/>). This ensures that between streaming sessions,
    /// the panel is re-rendered from the agent's <see cref="IAgent.Messages"/> list
    /// (which contains the completed assistant message), and the next streaming session
    /// starts with fresh UI elements.
    /// </summary>
    /// <summary>
    /// Handles a streaming token for a spawn_agent's inline section, updating
    /// the header preview and the response/thinking content inside the section.
    /// Called from the sub-agent's token callback during parallel execution.
    /// Always runs on the UI thread (the callback dispatches to it).
    /// </summary>
    private void HandleSpawnAgentInlineToken(SpawnAgentInlineContext ctx, AiStreamToken token)
    {
        bool toolsEnabled = _activeMode?.ToolsEnabled == true;

        switch (token.Type)
        {
            case AiStreamTokenType.Content:
                ctx.ResponseBuilder.Append(token.Text);
                ctx.ContentTokenCount++;
                ctx.HasContent = true;

                string displayedContent = GetVisibleAssistantContent(
                    ctx.ResponseBuilder.ToString(), toolsEnabled);
                ctx.ResponsePresenter.ReplaceAll(displayedContent);

                SetInlineSectionHeaderNoPinnedUpdate(
                    ctx.Section,
                    $"Sub-agent: {ctx.DisplayName}  •  {ctx.ContentTokenCount:N0} tokens");
                UpdateStreamingContentPreview(ctx.Section, GetStreamingPreview(
                    ctx.ResponseBuilder.ToString()));

                if (IsMessageScrollerNearBottom())
                {
                    MessageScroller.ScrollToEnd();
                }
                break;

            case AiStreamTokenType.Reasoning:
                ctx.ReasoningBuilder.Append(token.Text);
                ctx.ReasoningTokenCount++;
                ctx.HasThinking = true;

                if (ctx.ThinkingPresenter is null)
                {
                    (StreamSectionVisual thinkingSection, ChunkedTextPresenter thinkingPresenter) =
                        CreateThinkingSection(ctx.Section.ContentPanel, insertBefore: null);
                    ctx.ThinkingSection = thinkingSection;
                    ctx.ThinkingPresenter = thinkingPresenter;
                }

                ctx.ThinkingPresenter.Append(token.Text);
                SetInlineSectionHeaderNoPinnedUpdate(
                    ctx.ThinkingSection!,
                    $"Thinking ({ctx.ReasoningTokenCount:N0} tokens)...");
                UpdateStreamingContentPreview(ctx.ThinkingSection!,
                    GetStreamingPreview(ctx.ReasoningBuilder.ToString()));

                if (IsMessageScrollerNearBottom())
                {
                    MessageScroller.ScrollToEnd();
                }
                break;

            case AiStreamTokenType.ToolCall:
                // Sub-agent tool calls are rendered in the content area when
                // the tool completes; during streaming we just note it in the header.
                if (token.ToolCall is { } toolCall)
                {
                    string note = !string.IsNullOrWhiteSpace(toolCall.FunctionName)
                        ? $"  •  {toolCall.FunctionName}..."
                        : "  •  tool call...";
                    SetInlineSectionHeaderNoPinnedUpdate(
                        ctx.Section,
                        $"Sub-agent: {ctx.DisplayName}{note}");
                }
                break;
        }
    }

    /// <summary>
    /// Finalizes a spawn_agent inline section after the sub-agent completes.
    /// Updates the header to show the final token count and the completion status.
    /// </summary>
    private void FinalizeSpawnAgentSection(SpawnAgentInlineContext ctx, bool success)
    {
        string status = success ? "✓ completed" : "✗ failed";
        string header = ctx.ContentTokenCount > 0
            ? $"Sub-agent: {ctx.DisplayName}  •  {ctx.ContentTokenCount:N0} tokens  •  {status}"
            : $"Sub-agent: {ctx.DisplayName}  •  {status}";

        SetInlineSectionHeaderNoPinnedUpdate(ctx.Section, header);

        // Finalize thinking header if present
        if (ctx.ThinkingSection is not null && ctx.HasThinking)
        {
            SetInlineSectionHeaderNoPinnedUpdate(
                ctx.ThinkingSection!,
                ctx.ReasoningTokenCount > 0
                    ? $"Thought for {ctx.ReasoningTokenCount:N0} tokens"
                    : "Thought");
        }
    }

    private void RenderAgentStreamToken(string agentId, AiStreamToken token)
    {
        if (_viewingAgentId != agentId)
        {
            return;
        }

        if (!_agentStreamingStates.TryGetValue(agentId, out AgentStreamingState? state))
        {
            state = new AgentStreamingState();
            _agentStreamingStates[agentId] = state;
        }

        // If the panel was cleared (e.g., by RefreshAgentViewMessages or SwitchToAgentView),
        // the assistant container may no longer be in the panel. Reset the streaming state
        // so new UI elements are created for the current streaming session.
        if (state.AssistantContainer is not null &&
            !MessagePanel.Children.Contains(state.AssistantContainer))
        {
            state.AssistantContainer = null;
            state.AssistantBlock = null;
            state.ThinkingSection = null;
            state.ThinkingPresenter = null;
            state.ToolCallBlocks.Clear();
            state.HasContent = false;
            state.HasThinking = false;
        }

        bool toolsEnabled = _activeMode?.ToolsEnabled == true;

        switch (token.Type)
        {
            case AiStreamTokenType.Content:
                state.ResponseBuilder.Append(token.Text);
                state.ContentTokenCount++;

                if (!state.HasContent)
                {
                    state.HasContent = true;
                    (state.AssistantContainer, state.AssistantBlock) = CreateAssistantMessageBlock();
                }

                string displayedContent = GetVisibleAssistantContent(state.ResponseBuilder.ToString(), toolsEnabled);
                RenderAssistantContent(state.AssistantBlock!, displayedContent);
                UpdateStatsBar(state.ReasoningTokenCount + state.ContentTokenCount, state.Stopwatch);

                if (IsMessageScrollerNearBottom())
                {
                    MessageScroller.ScrollToEnd();
                }
                break;

            case AiStreamTokenType.Reasoning:
                state.ReasoningBuilder.Append(token.Text);
                state.ReasoningTokenCount++;

                if (!state.HasThinking)
                {
                    state.HasThinking = true;
                    if (state.AssistantContainer is null)
                    {
                        (state.AssistantContainer, state.AssistantBlock) = CreateAssistantMessageBlock();
                    }

                    (state.ThinkingSection, state.ThinkingPresenter) = CreateThinkingSection(
                        state.AssistantContainer, state.AssistantBlock);
                }

                state.ThinkingPresenter!.Append(token.Text);
                SetInlineSectionHeaderNoPinnedUpdate(
                    state.ThinkingSection!,
                    $"Thinking ({state.ReasoningTokenCount:N0} tokens)...");
                UpdateStreamingContentPreview(state.ThinkingSection!, state.ReasoningBuilder.ToString());
                UpdateStatsBar(state.ReasoningTokenCount + state.ContentTokenCount, state.Stopwatch);

                if (IsMessageScrollerNearBottom())
                {
                    MessageScroller.ScrollToEnd();
                }
                break;

            case AiStreamTokenType.ToolCall:
                if (token.ToolCall is null)
                {
                    break;
                }

                AiStreamToolCall toolCall = token.ToolCall;

                if (!state.ToolCallBlocks.TryGetValue(toolCall.Index, out ToolCallSectionVisual? block))
                {
                    if (state.AssistantContainer is null)
                    {
                        (state.AssistantContainer, state.AssistantBlock) = CreateAssistantMessageBlock();
                    }

                    block = CreateToolCallBlock(
                        toolCall.FunctionName,
                        toolCall.ArgumentsJson,
                        state.AssistantContainer,
                        state.AssistantBlock);
                    state.ToolCallBlocks[toolCall.Index] = block;
                }
                else
                {
                    UpdateToolCallBlock(block, toolCall.FunctionName, toolCall.ArgumentsJson);
                }

                if (IsMessageScrollerNearBottom())
                {
                    MessageScroller.ScrollToEnd();
                }
                break;
        }
    }

    /// <summary>
    /// Enables or disables the chat input controls.
    /// When disabled (e.g. viewing a completed sub-agent), the user cannot
    /// send new messages but can still scroll through the conversation.
    /// </summary>
    private void SetInputEnabled(bool enabled)
    {
        InputBox.IsEnabled = enabled;
        ExpandButton.IsEnabled = enabled;
        AddReferenceButton.IsEnabled = enabled;
        PasteImageButton.IsEnabled = enabled;
        ModeSelector.IsEnabled = enabled;
        SettingsOptionsButton.IsEnabled = enabled;
        FormattingOptionsButton.IsEnabled = enabled;
        ModelPickerButton.IsEnabled = enabled;
        ConversationSelector.IsEnabled = enabled;
        NewConversationButton.IsEnabled = enabled;
        ClearButton.IsEnabled = enabled;

        // When the input is disabled, change its background to visually
        // indicate it cannot accept input.
        if (enabled)
        {
            InputBox.Background = FindBrush("AiChatInputBackground");
        }
        else
        {
            InputBox.Background = FindBrush("AiChatHeaderBackground");
        }
    }

    /// <summary>
    /// Handler for the "◀ Back" button shown when viewing a sub-agent.
    /// Returns the chat panel to the normal conversation view.
    /// </summary>
    private void BackToChatButton_Click(object sender, RoutedEventArgs e)
    {
        SwitchToNormalView();
    }

    /// <summary>
    /// Removes all sub-agents from the orchestrator, keeping the root agent
    /// if one exists. Called when clearing the conversation or starting a new one.
    /// </summary>
    private void RemoveAllSubAgents()
    {
        if (_agentOrchestrator is null)
        {
            return;
        }

        IReadOnlyCollection<Services.Ai.Agents.IAgent> allAgents = _agentOrchestrator.GetAllAgents();

        foreach (Services.Ai.Agents.IAgent agent in allAgents)
        {
            if (agent.Role == Services.Ai.Agents.AgentRole.Root)
            {
                continue;
            }

            _agentOrchestrator.RemoveAgent(agent.Id);
        }
    }

    private void ConversationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingConversationSelection || ConversationSelector.SelectedItem is not AiConversation selectedConversation)
        {
            return;
        }

        if (_activeConversation is not null && string.Equals(_activeConversation.Id, selectedConversation.Id, StringComparison.Ordinal))
        {
            return;
        }

        // Remove sub-agents from the old conversation and reset view
        RemoveAllSubAgents();
        SwitchToNormalView();

        _activeConversation = selectedConversation;
        _conversationState.ActiveConversationId = selectedConversation.Id;
        _rawRequestPayloads.Clear();
        RenderActiveConversation();
        SavePersistedConversation();
        RefreshToolsCheckboxPanel();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    /// <summary>
    /// Opens the expanded input popup window. While the popup is open, the inline
    /// input box and send button are hidden. When the popup closes (either by sending
    /// or dismissing), the inline controls are restored.
    /// </summary>
    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        var popup = new AiChatInputWindow(InputBox.Text, Window.GetWindow(this));

        // Wire up the send event: populate the main input and trigger sending
        popup.SendRequested += async text =>
        {
            InputBox.Text = text;
            await SendMessageAsync();
        };

        // Allow pasting images into the expanded input window (Ctrl+V) just like the inline box.
        popup.InputBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.V &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                PasteImageFromClipboard();
            }
        };

        // Hide the inline input controls while the popup is open
        InputPanelContainer.Visibility = Visibility.Collapsed;

        popup.Closed += (_, _) =>
        {
            // Restore the inline input controls when the popup is dismissed
            InputPanelContainer.Visibility = Visibility.Visible;

            // If the user closed without sending, sync any typed text back
            if (popup.DialogResult != true && !string.IsNullOrWhiteSpace(popup.InputBox.Text))
            {
                InputBox.Text = popup.InputBox.Text;
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        };

        popup.ShowDialog();
    }

    /// <summary>
    /// Builds a <see cref="ContextMenu"/> for a spell-check-enabled <see cref="TextBox"/>.
    /// If the text under the cursor has a spelling error, suggestions are shown at the top
    /// followed by a separator. Standard editing commands (Undo, Redo, Cut, Copy, Paste,
    /// Delete, Select All) are always included.
    /// </summary>
    internal static ContextMenu BuildSpellCheckContextMenu(TextBox textBox)
    {
        ArgumentNullException.ThrowIfNull(textBox);

        var menu = new ContextMenu();

        // Determine the character index under the mouse cursor
        var mousePos = Mouse.GetPosition(textBox);
        int charIndex = textBox.GetCharacterIndexFromPoint(mousePos, snapToText: true);

        if (charIndex >= 0)
        {
            var spellingError = textBox.GetSpellingError(charIndex);
            if (spellingError is not null)
            {
                bool hasSuggestions = false;
                // ReSharper disable once LoopCanBeConvertedToQuery
                foreach (string suggestion in spellingError.Suggestions)
                {
                    hasSuggestions = true;
                    var item = new MenuItem
                    {
                        Header = suggestion,
                        FontWeight = FontWeights.Bold,
                        Command = EditingCommands.CorrectSpellingError,
                        CommandParameter = suggestion,
                        CommandTarget = textBox
                    };
                    menu.Items.Add(item);
                }

                if (hasSuggestions)
                {
                    menu.Items.Add(new Separator());
                }
            }
        }

        // Standard editing commands
        menu.Items.Add(new MenuItem { Header = "Undo", Command = ApplicationCommands.Undo });
        menu.Items.Add(new MenuItem { Header = "Redo", Command = ApplicationCommands.Redo });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Cut", Command = ApplicationCommands.Cut });
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy });
        menu.Items.Add(new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste });
        menu.Items.Add(new MenuItem { Header = "Delete", Command = ApplicationCommands.Delete });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Select All", Command = ApplicationCommands.SelectAll });

        return menu;
    }

    private void InputBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ContextMenu = BuildSpellCheckContextMenu(textBox);
        }
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

    /// <summary>
    /// Intercepts the Paste command at the preview (tunneling) stage.
    /// If the clipboard contains a bitmap, the paste is cancelled and the
    /// image is saved as a temp file and added as an image reference.
    /// </summary>
    private void OnInputBoxPreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste)
        {
            return;
        }

        bool hasImage = Clipboard.ContainsImage();

        // Try GetImage directly — it handles more formats than ContainsImage
        BitmapSource? directImage = Clipboard.GetImage();

        if (!hasImage && directImage is null)
        {
            return;
        }

        e.Handled = true;
        HandleClipboardImagePaste();
    }

    /// <summary>
    /// Intercepts paste operations via the Pasting attached event.
    /// If the clipboard contains a bitmap, cancels the text paste and
    /// handles the image. Matches the standard WPF recipe:
    /// DataObject.AddPastingHandler + e.SourceDataObject + e.CancelCommand.
    /// </summary>
    private void OnInputBoxPaste(object sender, DataObjectPastingEventArgs e)
    {
        bool hasBitmap = e.SourceDataObject.GetDataPresent(DataFormats.Bitmap);
        bool hasImage = Clipboard.ContainsImage();

        if (hasBitmap || hasImage)
        {
            e.CancelCommand();
            e.Handled = true;
            HandleClipboardImagePaste();
        }
    }

    /// <summary>
    /// Reads a bitmap image from the clipboard, persists it to a temp file,
    /// and adds it as an <see cref="AiReferenceKind.Image"/> reference.
    /// When the current provider does not support images, the paste is
    /// silently ignored and a system message warns the user.
    /// </summary>
    internal void HandleClipboardImagePaste()
    {
        // Try GetImage first — it handles more formats than ContainsImage
        BitmapSource? image = Clipboard.GetImage();

        if (image is null)
        {
            // Last resort: try to get PNG or DIB bytes directly
            IDataObject? dataObj = Clipboard.GetDataObject();
            if (dataObj is not null)
            {
                // Try PNG format first
                if (dataObj.GetDataPresent("PNG", true))
                {
                    object? pngRaw = dataObj.GetData("PNG", true);
                    if (pngRaw is byte[] pngBytes)
                    {
                        SaveImageBytes(pngBytes, "image/png");
                        return;
                    }
                    else if (pngRaw is MemoryStream pngStream)
                    {
                        SaveImageBytes(pngStream.ToArray(), "image/png");
                        return;
                    }
                }

                // Try DIB
                if (dataObj.GetDataPresent(DataFormats.Dib, true))
                {
                    object? dibRaw = dataObj.GetData(DataFormats.Dib, true);
                    if (dibRaw is byte[] dibBytes)
                    {
                        // DIB to BitmapSource conversion
                        BitmapSource? dibImage = ConvertDibToBitmapSource(dibBytes);
                        if (dibImage is not null)
                        {
                            image = dibImage;
                        }
                    }
                }

                // Try DeviceIndependentBitmap
                if (image is null && dataObj.GetDataPresent("DeviceIndependentBitmap", true))
                {
                    object? dibRaw = dataObj.GetData("DeviceIndependentBitmap", true);
                    if (dibRaw is byte[] dibBytes)
                    {
                        image = ConvertDibToBitmapSource(dibBytes);
                    }
                }
            }

            if (image is null)
            {
                return;
            }
        }

        bool providerSupportsImages = _provider?.SupportsImages == true;

        if (!providerSupportsImages)
        {
            AppendSystemMessage("The current AI provider does not support images. Paste ignored.");
            return;
        }

        try
        {
            // Encode to PNG in memory
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(image));

            byte[] pngBytes;
            using (MemoryStream stream = new())
            {
                encoder.Save(stream);
                pngBytes = stream.ToArray();
            }

            // Save to temp file with a unique name
            string tempDir = Path.GetTempPath();
            string fileName = $"kane_paste_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}.png";
            string filePath = Path.Combine(tempDir, fileName);
            File.WriteAllBytes(filePath, pngBytes);

            AiChatReference imageRef = AiContextReferenceFactory.CreateImageReference(filePath);
            AddReference(imageRef);

            AppendSystemMessage($"📋 Pasted image added as reference: {fileName}");
        }
        catch (IOException ex)
        {
            AppendSystemMessage($"Failed to save pasted image: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendSystemMessage($"Failed to save pasted image: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves raw image bytes directly to a temp file and adds as reference.
    /// </summary>
    private void SaveImageBytes(byte[] bytes, string mimeType)
    {
        string extension = mimeType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".png"
        };

        string tempDir = Path.GetTempPath();
        string fileName = $"kane_paste_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}{extension}";
        string filePath = Path.Combine(tempDir, fileName);
        File.WriteAllBytes(filePath, bytes);

        AiChatReference imageRef = AiContextReferenceFactory.CreateImageReference(filePath);
        AddReference(imageRef);

        AppendSystemMessage($"📋 Pasted image added as reference: {fileName}");
    }

    /// <summary>
    /// Converts raw DIB (Device Independent Bitmap) bytes to a <see cref="BitmapSource"/>.
    /// </summary>
    private static BitmapSource? ConvertDibToBitmapSource(byte[] dibBytes)
    {
        try
        {
            using MemoryStream stream = new(dibBytes);
            BmpBitmapDecoder decoder = new(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                return decoder.Frames[0];
            }
        }
        catch
        {
            // DIB conversion failed — return null
        }

        return null;
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

    private void RenderActiveConversation()
    {
        // Guard: if viewing a sub-agent, don't overwrite with conversation content
        if (_viewingAgentId is not null)
        {
            return;
        }

        AiConversation conversation = EnsureActiveConversation();
        CancelStreaming();
        _pendingSelectionContext = null;
        _streamSections.Clear();
        _inlineImageBorders.Clear();
        MessagePanel.Children.Clear();
        PinnedSectionPanel.Children.Clear();
        PinnedSectionPanel.Visibility = Visibility.Collapsed;
        RenderReferenceTags();

        if (IsRawTextModeEnabled())
        {
            RebuildRawConversation(conversation);
        }
        else
        {
            RebuildNormalConversation(conversation);
        }

        RefreshContextWindowDisplay();
        UpdatePinnedSectionHeaders();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        CancelStreaming();

        // Remove all sub-agents (keep root if it exists)
        RemoveAllSubAgents();
        SwitchToNormalView();

        AiConversation conversation = EnsureActiveConversation();
        conversation.Messages.Clear();
        conversation.References.Clear();
        conversation.ProjectContextInjected = false;
        if (string.Equals(conversation.Title, "Imported conversation", StringComparison.OrdinalIgnoreCase) ||
            conversation.Title.StartsWith("Conversation ", StringComparison.OrdinalIgnoreCase))
        {
            conversation.Title = "New conversation";
        }

        _rawRequestPayloads.Clear();
        TouchConversation(conversation);
        RefreshConversationSelector();
        RenderActiveConversation();
        SavePersistedConversation();
    }

    /// <summary>
    /// Called when the model picker toggle button is clicked.
    /// Toggles the visibility of the provider/model overlay panel.
    /// </summary>
    private void ModelPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelPickerOverlay.Visibility == Visibility.Visible)
        {
            CloseModelPickerOverlay();
        }
        else
        {
            OpenModelPickerOverlay();
        }
    }

    /// <summary>
    /// Opens the overlay panel and populates the provider and model lists.
    /// </summary>
    private void OpenModelPickerOverlay()
    {
        PopulateProviderList();

        // Reset the search box so a previously typed filter does not persist
        // when re-opening the picker for a different provider.
        ModelSearchBox.Text = string.Empty;

        // Select the active provider from the registry
        IAiProvider? activeProvider = _providerRegistry?.ActiveProvider;
        if (activeProvider is not null && ProviderListBox.Items.Count > 0)
        {
            ProviderListBox.SelectedItem = activeProvider;
        }

        ModelPickerOverlay.Visibility = Visibility.Visible;

        // Focus the search box so the user can immediately start typing to filter.
        ModelSearchBox.Focus();
    }

    private void CloseModelPickerOverlay()
    {
        ModelPickerOverlay.Visibility = Visibility.Collapsed;
        ModelPickerButton.IsChecked = false;
    }

    /// <summary>
    /// Populates the provider list from the registry.
    /// Creates a snapshot (<see cref="Enumerable.ToList{T}"/>) so that WPF never
    /// tracks the live <c>List&lt;IAiProvider&gt;</c> that <see cref="AiProviderRegistry.Reload"/>
    /// mutates in-place (Clear + Add) without <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>
    /// notifications. Without this snapshot, WPF's <c>ItemContainerGenerator</c> detects
    /// the inconsistency during the next layout pass and throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    private void PopulateProviderList()
    {
        if (_providerRegistry is null || _providerRegistry.Providers.Count == 0)
        {
            ProviderListBox.ItemsSource = null;
            return;
        }

        // ToList() creates a snapshot — WPF binds to this copy, not the live list.
        ProviderListBox.ItemsSource = _providerRegistry.Providers.ToList();
    }

    /// <summary>
    /// Called when the selected provider in the overlay list changes.
    /// Sets the active provider, restores the saved model, and fetches the model list asynchronously.
    /// The button text is updated immediately so the user sees the new provider name right away,
    /// rather than retaining the previous provider's label while models are being discovered.
    /// </summary>
    private async void ProviderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderListBox.SelectedItem is not IAiProvider selected)
        {
            return;
        }

        try
        {
            _providerRegistry?.SetActiveProvider(selected);
            Configure(selected, _providerRegistry?.GetSettings(selected)?.SelectedModel);
            UpdateModelPickerButtonText();
            await RefreshOverlayModelListAsync(selected);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, "Failed to switch AI provider.");
            AppendSystemMessage($"Error switching provider: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when the selected model in the overlay list changes.
    /// Persists the selection immediately so it survives application restarts.
    /// </summary>
    private void ModelListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isProgrammaticModelSelection)
        {
            return;
        }

        if (ModelListBox.SelectedItem is not string selectedModel)
        {
            return;
        }

        if (string.Equals(_model, selectedModel, StringComparison.Ordinal))
        {
            return;
        }

        _model = selectedModel;
        UpdateModelPickerButtonText();
        SyncRootAgentConfig();

        if (_provider is not null && _providerRegistry is not null)
        {
            AiProviderSettings? settings = _providerRegistry.GetSettings(_provider);
            if (settings is not null)
            {
                settings.SelectedModel = selectedModel;
                AiSettingsManager.Save(_providerRegistry.Providers
                    .Select(p => _providerRegistry.GetSettings(p))
                    .Where(s => s is not null)
                    .Cast<AiProviderSettings>()
                    .ToList(),
                    raiseEvent: false);
            }
        }
    }

    /// <summary>
    /// Called when the close button in the overlay is clicked.
    /// Dismisses the model/provider picker overlay.
    /// </summary>
    private void ModelPickerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseModelPickerOverlay();
    }

    /// <summary>
    /// The full, unfiltered model list for the currently selected provider.
    /// Kept separately from <see cref="ModelListBox"/>'s <see cref="ItemsControl.ItemsSource"/>
    /// so the search box can re-filter without re-fetching models from the provider.
    /// </summary>
    private IReadOnlyList<string> _overlayModelList = [];

    /// <summary>
    /// Set while the search box programmatically re-highlights a model so that
    /// <see cref="ModelListBox_SelectionChanged"/> does not treat the highlight as an
    /// explicit user selection (which would change and persist the active model).
    /// </summary>
    private bool _isProgrammaticModelSelection;

    /// <summary>
    /// Called when the text in the model search box changes. Filters the model list
    /// box to models whose names contain the search term (case-insensitive). The
    /// active model is only changed when the user explicitly clicks a model, not while
    /// they are typing a filter — if the highlighted model is filtered out, the first
    /// match is highlighted without committing it.
    /// </summary>
    private void ModelSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_overlayModelList.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> filtered = FilterModels(_overlayModelList, ModelSearchBox.Text);
        ModelListBox.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            return;
        }

        // Keep the current selection if it is still visible after filtering.
        string? currentSelection = ModelListBox.SelectedItem as string;
        if (currentSelection is not null && filtered.Contains(currentSelection, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // If the search was cleared, restore the highlight to the active model.
        if (string.IsNullOrWhiteSpace(ModelSearchBox.Text) &&
            _model is not null &&
            filtered.Contains(_model, StringComparer.OrdinalIgnoreCase))
        {
            ModelListBox.SelectedItem = _model;
            return;
        }

        // Otherwise highlight the first match for keyboard/click navigation without
        // committing it as the active model (no persistence, no button-text change).
        _isProgrammaticModelSelection = true;
        try
        {
            ModelListBox.SelectedItem = filtered[0];
        }
        finally
        {
            _isProgrammaticModelSelection = false;
        }
    }

    /// <summary>
    /// Returns the subset of <paramref name="models"/> whose names contain
    /// <paramref name="searchTerm"/> (case-insensitive). An empty or whitespace
    /// search term returns the list unchanged.
    /// </summary>
    private static IReadOnlyList<string> FilterModels(IReadOnlyList<string> models, string? searchTerm)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return models;
        }

        string normalizedTerm = searchTerm.Trim();
        List<string> matches = new(models.Count);

        foreach (string model in models)
        {
            if (model.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(model);
            }
        }

        return matches;
    }

    /// <summary>
    /// Dismisses the overlay when clicking on the background border
    /// (but not on the list items inside it).
    /// </summary>
    private void ModelPickerOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
        {
            CloseModelPickerOverlay();
        }
    }

    /// <summary>
    /// Updates the model picker button text.  When a model is known and the provider
    /// has models available, only the model name is displayed.  Otherwise the provider
    /// name is shown (e.g. when no models have been discovered yet).
    /// </summary>
    private void UpdateModelPickerButtonText()
    {
        if (_provider is null)
        {
            ModelPickerButton.Content = "No provider";
            return;
        }

        bool hasModels = _provider.AvailableModels.Count > 0;
        bool hasModelName = !string.IsNullOrWhiteSpace(_model);

        if (hasModels && hasModelName)
        {
            ModelPickerButton.Content = _model;
        }
        else
        {
            ModelPickerButton.Content = _provider.DisplayName;
        }
    }

    /// <summary>
    /// Refreshes the provider selection from the current registry state.
    /// Called after settings change or on initial load.
    /// </summary>
    private async void RefreshProviderSelector()
    {
        if (_providerRegistry is null)
        {
            return;
        }

        try
        {
            IAiProvider? active = _providerRegistry.ActiveProvider;
            if (active is not null)
            {
                Configure(active, _providerRegistry.GetSettings(active)?.SelectedModel);
                UpdateModelPickerButtonText();
                await RefreshOverlayModelListAsync(active);
            }
            else if (_providerRegistry.Providers.Count > 0)
            {
                IAiProvider firstProvider = _providerRegistry.Providers[0];
                Configure(firstProvider, _providerRegistry.GetSettings(firstProvider)?.SelectedModel);
                UpdateModelPickerButtonText();
                await RefreshOverlayModelListAsync(firstProvider);
            }
            else
            {
                Configure(null);
                ModelListBox.ItemsSource = null;
                UpdateModelPickerButtonText();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, "Failed to refresh provider selector.");
            AppendSystemMessage($"Error loading AI provider: {ex.Message}");
        }
    }

    private void ProviderRegistry_ProvidersChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ProviderRegistry_ProvidersChanged, sender, e);
            return;
        }

        // Re-bind the provider list to a fresh snapshot so WPF never sees the
        // live list that AiProviderRegistry.Reload mutates in-place.
        PopulateProviderList();

        RefreshProviderSelector();
    }

    /// <summary>
    /// Refreshes the model list for the given provider.
    /// Shows currently known models immediately, then fetches fresh models asynchronously.
    /// </summary>
    private async Task RefreshOverlayModelListAsync(IAiProvider provider)
    {
        CancelModelDiscovery();

        // Show currently known models immediately (may be empty if not yet discovered)
        ApplyOverlayModelList(provider.AvailableModels);

        // Then attempt async discovery
        CancellationTokenSource cts = new();
        _modelDiscoveryCts = cts;

        IReadOnlyList<string> discoveredModels;
        try
        {
            discoveredModels = await provider.GetAvailableModelsAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, "AI model discovery failed.");
            return;
        }

        if (_modelDiscoveryCts != cts)
        {
            return;
        }

        ApplyOverlayModelList(discoveredModels);
    }

    /// <summary>
    /// Applies a list of models to the model list box and selects the best match.
    /// When the list is empty, the model list is cleared so it always reflects the
    /// selected provider's models.  Selection and button-text updates are skipped
    /// for empty results; the button text was already updated by
    /// <see cref="ProviderListBox_SelectionChanged"/> before discovery started.
    /// </summary>
    private void ApplyOverlayModelList(IReadOnlyList<string> models)
    {
        // Store the full list so the search box can re-filter without re-fetching.
        _overlayModelList = models;

        IReadOnlyList<string> visibleModels = FilterModels(models, ModelSearchBox.Text);
        ModelListBox.ItemsSource = visibleModels;

        if (models.Count == 0)
        {
            return;
        }

        string? selectedModel = SelectModel(models, _model);
        ModelListBox.SelectedItem = selectedModel;
        _model = selectedModel;
        UpdateModelPickerButtonText();
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
        if (_isUpdatingModeSelection)
        {
            return;
        }

        if (ModeSelector.SelectedItem is ModeDropdownModeItem modeItem)
        {
            IAiChatMode mode = modeItem.Mode;
            _activeMode = mode;

            if (mode.Id != "custom")
            {
                ApplyModePreset(mode);
            }

            AppendSystemMessage($"Switched to {mode.DisplayName} mode.");
            RefreshToolsCheckboxPanel();
            RefreshContextWindowDisplay();
            SyncRootAgentConfig();
        }
        else if (ModeSelector.SelectedItem is ModeDropdownPresetItem presetItem)
        {
            _activeMode = presetItem.Mode;
            ApplyModePreset(presetItem.Mode);
            AppendSystemMessage($"Switched to preset \"{presetItem.Preset.Name}\" mode.");
            RefreshToolsCheckboxPanel();
            RefreshContextWindowDisplay();
            SyncRootAgentConfig();
        }
        else if (ModeSelector.SelectedItem is ModeDropdownActionItem actionItem)
        {
            // Invoke the action and restore the previous selection
            actionItem.Action?.Invoke();
        }
    }

    /// <summary>
    /// Applies a preset mode's default tools and system prompt to the active
    /// conversation, and records it as the <see cref="AiConversation.BaseModeId"/>.
    /// </summary>
    private void ApplyModePreset(IAiChatMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);

        AiConversation conversation = EnsureActiveConversation();

        // Reset tools to mode defaults
        if (!mode.ToolsEnabled || _toolRegistry is null || !_toolRegistry.HasTools)
        {
            conversation.EnabledTools = [];
        }
        else
        {
            IReadOnlySet<string>? allowed = mode.AllowedTools;
            conversation.EnabledTools = allowed is null
                ? _toolRegistry.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal)
                : _toolRegistry.Tools
                    .Select(t => t.Name)
                    .Where(allowed.Contains)
                    .ToHashSet(StringComparer.Ordinal);
        }

        // Reset system prompt to mode default
        JsonElement toolsDef = _toolRegistry is not null
            ? _toolRegistry.SerializeToolDefinitions(conversation.EnabledTools)
            : default;
        conversation.SystemPrompt = mode.BuildSystemPrompt(toolsDef);

        // Record this as the base preset
        conversation.BaseModeId = mode.Id;
    }

    /// <summary>
    /// Returns the effective system prompt for the active conversation.
    /// Uses the conversation's stored prompt if set, otherwise falls back
    /// to the active mode's <see cref="IAiChatMode.BuildSystemPrompt"/>.
    /// </summary>
    private string? GetEffectiveSystemPrompt(JsonElement toolsDef)
    {
        AiConversation conversation = EnsureActiveConversation();
        return conversation.SystemPrompt ?? _activeMode?.BuildSystemPrompt(toolsDef);
    }

    private async Task SendMessageAsync()
    {
        // Guard: prevent sending messages while viewing a sub-agent's conversation
        if (_viewingAgentId is not null)
        {
            return;
        }

        string? text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        AiConversation conversation = EnsureActiveConversation();

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
        if (!conversation.ProjectContextInjected)
        {
            IReadOnlyList<ProjectItem> projectItems = _projectItemsProvider?.Invoke() ?? [];
            string projectContext = AiProjectContextBuilder.Build(projectItems);

            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                conversation.Messages.Add(new AiChatMessage(AiChatRole.System, projectContext));
            }

            conversation.ProjectContextInjected = true;
        }

        EnsureConversationTitle(conversation, text);

        List<AiChatReference> pendingReferences = [.. conversation.References];
        List<string> pendingExternalDirectories = pendingReferences
            .Where(reference => reference.Kind == AiReferenceKind.ExternalFolder)
            .Select(reference => reference.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Collect image references for vision-capable providers
        List<AiChatImagePart> pendingImages = [];
        bool providerSupportsImages = _provider?.SupportsImages == true;
        if (providerSupportsImages)
        {
            foreach (AiChatReference imageRef in pendingReferences.Where(r => r.Kind == AiReferenceKind.Image))
            {
                if (!string.IsNullOrEmpty(imageRef.Content))
                {
                    string extension = System.IO.Path.GetExtension(imageRef.FullPath);
                    string mimeType = AiContextReferenceFactory.GetImageMimeType(extension);
                    pendingImages.Add(new AiChatImagePart(imageRef.Content, mimeType));
                }
            }
        }

        string pendingContext = BuildPendingPromptContext(pendingReferences, _pendingSelectionContext);

        _pendingSelectionContext = null;

        _externalContextDirectoryRegistry?.SetAllowedDirectories(pendingExternalDirectories);

        outboundUserContent = string.IsNullOrWhiteSpace(pendingContext)
            ? text
            : $"{pendingContext}\n\n{text}";

        displayedUserContent = GetDisplayedUserMessageContent(text, outboundUserContent, IsRawTextModeEnabled());

        AppendUserMessage(displayedUserContent);

        AiChatMessage userMessage = new(AiChatRole.User, text)
        {
            Images = pendingImages.Count > 0 ? pendingImages : null
        };
        conversation.Messages.Add(userMessage);
        TouchConversation(conversation);
        RefreshConversationSelector();
        List<AiChatMessage> requestConversationHistory = BuildRequestConversationHistory(conversation.Messages, outboundUserContent);

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
        _aggregatedUsageStats = null;

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
            JsonElement toolsDef = GetToolDefinitionsForConversation();

            int iteration = 0;

            while (iteration < MaxToolCallIterations)
            {
                iteration++;
                ct.ThrowIfCancellationRequested();

                System.Text.StringBuilder responseBuilder = new();
                System.Text.StringBuilder reasoningBuilder = new();
                StreamSectionVisual? thinkingSection = null;
                ChunkedTextPresenter? thinkingPresenter = null;
                TextBlock? rawThinkingBlock = null;
                Dictionary<int, AiStreamToolCall> streamedToolCalls = new();
                Dictionary<int, ToolCallSectionVisual> toolCallBlocks = new();
                Dictionary<int, TextBlock> rawToolCallBlocks = new();

                // UI element creation must happen on the dispatcher thread.
                // After the first iteration, we may be on a thread-pool thread
                // due to ConfigureAwait(false) in tool execution.
                (StackPanel assistantContainer, RichTextBox assistantBlock) = await Dispatcher.InvokeAsync(CreateAssistantMessageBlock);

                bool toolsEnabled = _activeMode?.ToolsEnabled == true;
                int outboundTokenBudget = GetOutboundTokenBudget();
                AiContextWindowSnapshot contextWindow = AiContextWindowBuilder.Build(requestConversationHistory, outboundTokenBudget, toolsEnabled);
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

                /* Capture the raw request JSON payload before sending to the provider.
                 * We capture every iteration (including tool-calling loop iterations)
                 * even when raw mode is off, so the user can toggle it on at any time
                 * and see the full history. */
                if (_provider is V1CompletionsProvider v1CompletionsProvider)
                {
                    string rawJson = v1CompletionsProvider.BuildRawRequestJson(
                        outboundMessages,
                        model,
                        toolsDef,
                        streamResponses);
                    string endpointUrl = v1CompletionsProvider.GetChatCompletionEndpoint();
                    _rawRequestPayloads.Add(new RawRequestPayload(
                        endpointUrl,
                        model,
                        rawJson,
                        ResponseContent: null,
                        ReasoningContent: null));
                }
                else if (_provider is V1ChatCompletionsProvider v1ChatCompletionsProvider)
                {
                    string rawJson = v1ChatCompletionsProvider.BuildRawRequestJson(
                        outboundMessages,
                        model,
                        toolsDef,
                        streamResponses);
                    string endpointUrl = v1ChatCompletionsProvider.GetChatCompletionEndpoint();
                    _rawRequestPayloads.Add(new RawRequestPayload(
                        endpointUrl,
                        model,
                        rawJson,
                        ResponseContent: null,
                        ReasoningContent: null));
                }

                /* Batch accumulator groups rapid per-token dispatches into batches
                 * at a fixed interval (50ms), reducing UI thread overhead during
                 * high-speed streaming. Structural first-time creations use
                 * DispatchSync to ensure they complete before background code continues. */
                using var batch = new BatchAccumulator(Dispatcher, TimeSpan.FromMilliseconds(50));

                await foreach (AiStreamToken token in _provider.StreamCompletionAsync(outboundMessages, model, toolsDef, streamResponses, ct)
                    .ConfigureAwait(false))
                {
                    if (token.Type == AiStreamTokenType.Usage)
                    {
                        _aggregatedUsageStats = MergeUsageStats(_aggregatedUsageStats, token.UsageStats);
                        continue;
                    }

                    if (token.Type == AiStreamTokenType.ToolCall && token.ToolCall is not null)
                    {
                        AiStreamToolCall toolCall = token.ToolCall!;
                        streamedToolCalls[toolCall.Index] = toolCall;

                        if (streamingDisabled)
                        {
                            continue;
                        }

                        bool isFirstBlockForIndex = !toolCallBlocks.ContainsKey(toolCall.Index);

                        if (isFirstBlockForIndex)
                        {
                            /* First creation must complete before tool execution */
                            await batch.DispatchSync(() =>
                            {
                                if (rawTextMode)
                                {
                                    TextBlock rawBlock = AppendRawTranscriptEntry(
                                        $"Tool Call ({toolCall.FunctionName})",
                                        "Receiving arguments...",
                                        FindBrush(ThemeResourceKeys.AiChatToolCallForeground),
                                        assistantContainer);
                                    rawToolCallBlocks[toolCall.Index] = rawBlock;
                                    return;
                                }

                                // spawn_agent gets a dedicated inline section in Phase 1;
                                // skip creating a regular tool-call block during streaming
                                if (string.Equals(toolCall.FunctionName, "spawn_agent", StringComparison.Ordinal))
                                {
                                    return;
                                }

                                ToolCallSectionVisual block = CreateToolCallBlock(
                                    toolCall.FunctionName,
                                    toolCall.ArgumentsJson,
                                    assistantContainer,
                                    assistantBlock);
                                toolCallBlocks[toolCall.Index] = block;
                            });
                        }
                        else
                        {
                            /* Incremental updates — batch these */
                            batch.Enqueue(() =>
                            {
                                bool shouldStickToBottom = IsMessageScrollerNearBottom();

                                if (rawTextMode)
                                {
                                    if (shouldStickToBottom)
                                    {
                                        MessageScroller.ScrollToEnd();
                                    }

                                    return;
                                }

                                // spawn_agent has no visual block during streaming
                                if (!toolCallBlocks.TryGetValue(toolCall.Index, out ToolCallSectionVisual? block))
                                {
                                    return;
                                }

                                UpdateToolCallBlock(block, toolCall.FunctionName, toolCall.ArgumentsJson);

                                if (shouldStickToBottom)
                                {
                                    MessageScroller.ScrollToEnd();
                                }
                            });
                        }

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

                        bool isFirstReasoningToken = thinkingSection is null;

                        if (isFirstReasoningToken)
                        {
                            /* First creation must complete before content tokens arrive */
                            await batch.DispatchSync(() =>
                            {
                                bool shouldStickToBottom = IsMessageScrollerNearBottom();

                                if (rawTextMode)
                                {
                                    rawThinkingBlock = AppendRawTranscriptEntry(
                                        "Thinking",
                                        GetVisibleAssistantContent(reasoningBuilder.ToString(), toolsEnabled),
                                        FindBrush("AiChatThinkingForeground"),
                                        assistantContainer);
                                    UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                                    if (shouldStickToBottom)
                                    {
                                        MessageScroller.ScrollToEnd();
                                    }

                                    return;
                                }

                                (thinkingSection, thinkingPresenter) = CreateThinkingSection(assistantContainer, assistantBlock);
                                thinkingPresenter.Append(token.Text);
                                SetInlineSectionHeaderNoPinnedUpdate(thinkingSection, $"Thinking ({reasoningTokenCount:N0} tokens)...");
                                UpdateStreamingContentPreview(thinkingSection, reasoningBuilder.ToString());
                                UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                                if (shouldStickToBottom)
                                {
                                    MessageScroller.ScrollToEnd();
                                }
                            });
                        }
                        else
                        {
                            /* Incremental tokens — batch into periodic UI updates */
                            batch.Enqueue(() =>
                            {
                                thinkingPresenter!.Append(token.Text);
                                SetInlineSectionHeaderNoPinnedUpdate(thinkingSection!, $"Thinking ({reasoningTokenCount:N0} tokens)...");
                                UpdateStreamingContentPreview(thinkingSection!, reasoningBuilder.ToString());
                                UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                                if (IsMessageScrollerNearBottom())
                                {
                                    MessageScroller.ScrollToEnd();
                                }
                            });
                        }
                    }
                    else
                    {
                        responseBuilder.Append(token.Text);
                        contentTokenCount++;

                        if (streamingDisabled)
                        {
                            continue;
                        }

                        /* Content rendering is always incremental — the RichTextBox already exists */
                        batch.Enqueue(() =>
                        {
                            bool shouldStickToBottom = IsMessageScrollerNearBottom();
                            string displayedResponseContent = GetVisibleAssistantContent(responseBuilder.ToString(), toolsEnabled);

                            // Finalize the thinking header once content starts
                            if (thinkingSection is not null &&
                                thinkingSection.HeaderText.Text.EndsWith("...", StringComparison.Ordinal))
                            {
                                SetInlineSectionHeaderNoPinnedUpdate(thinkingSection, $"Thought for {reasoningTokenCount:N0} tokens");
                            }

                            RenderAssistantContent(assistantBlock, displayedResponseContent);
                            UpdatePinnedSectionHeaders();
                            UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                            if (shouldStickToBottom)
                            {
                                MessageScroller.ScrollToEnd();
                            }
                        });
                    }
                }

                /* Flush any remaining batched updates before processing tool results */
                await batch.FlushAsync();

                if (streamingDisabled)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        bool shouldStickToBottom = IsMessageScrollerNearBottom();
                        string displayedReasoningContent = GetVisibleAssistantContent(reasoningBuilder.ToString(), toolsEnabled);
                        string displayedResponseContent = GetVisibleAssistantContent(responseBuilder.ToString(), toolsEnabled);

                        if (rawTextMode)
                        {
                            if (!string.IsNullOrWhiteSpace(displayedReasoningContent))
                            {
                                AppendRawTranscriptEntry(
                                    "Thinking",
                                    displayedReasoningContent,
                                    FindBrush("AiChatThinkingForeground"),
                                    assistantContainer);
                            }

                            if (!string.IsNullOrWhiteSpace(displayedResponseContent))
                            {
                                AppendRawTranscriptEntry(
                                    "Assistant",
                                    displayedResponseContent,
                                    FindBrush("AiChatAssistantForeground"),
                                    assistantContainer);
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(displayedReasoningContent))
                            {
                                (thinkingSection, thinkingPresenter) = CreateThinkingSection(assistantContainer, assistantBlock);
                                thinkingPresenter.ReplaceAll(FormatDisplayedAssistantContent(
                                    displayedReasoningContent,
                                    ShouldRemoveVerticalWhitespace()));
                                SetInlineSectionHeaderNoPinnedUpdate(
                                    thinkingSection,
                                    reasoningTokenCount > 0
                                        ? $"Thought for {reasoningTokenCount:N0} tokens"
                                        : "Thought");
                            }

                            RenderAssistantContent(assistantBlock, displayedResponseContent);
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
                            SetInlineSectionHeaderNoPinnedUpdate(
                                thinkingSection,
                                reasoningTokenCount > 0
                                    ? $"Thought for {reasoningTokenCount:N0} tokens"
                                    : "Thought");
                        }
                    });
                }

                // Update the most recent raw payload with the accumulated response content
                if (_rawRequestPayloads.Count > 0)
                {
                    RawRequestPayload lastPayload = _rawRequestPayloads[^1];
                    _rawRequestPayloads[^1] = lastPayload with
                    {
                        ReasoningContent = reasoningBuilder.ToString(),
                        ResponseContent = responseBuilder.ToString()
                    };
                }

                List<RecoveredMalformedToolCall> recoveredMalformedToolCalls = _activeMode?.ToolsEnabled == true && streamedToolCalls.Count == 0
                    ? MalformedToolCallRecovery.Recover(reasoningBuilder.ToString(), responseBuilder.ToString()).ToList()
                    : [];
                string sanitizedReasoningContent = GetVisibleAssistantContent(reasoningBuilder.ToString(), toolsEnabled);
                string sanitizedResponseContent = GetVisibleAssistantContent(responseBuilder.ToString(), toolsEnabled);

                if (rawTextMode &&
                    string.IsNullOrWhiteSpace(sanitizedResponseContent) &&
                    streamedToolCalls.Count == 0 &&
                    recoveredMalformedToolCalls.Count == 0)
                {
                    await Dispatcher.InvokeAsync(() => MessagePanel.Children.Remove(assistantContainer));
                }

                List<AiStreamToolCall> pendingToolCalls = [];
                if (_activeMode?.ToolsEnabled == true)
                {
                    if (streamedToolCalls.Count > 0)
                    {
                        pendingToolCalls = streamedToolCalls
                            .OrderBy(kv => kv.Key)
                            .Select(kv => kv.Value)
                            .Where(tc => !string.IsNullOrWhiteSpace(tc.FunctionName))
                            .ToList();
                    }
                    else if (recoveredMalformedToolCalls.Count > 0)
                    {
                        pendingToolCalls = recoveredMalformedToolCalls
                            .Select(tc => new AiStreamToolCall(
                                tc.Index,
                                $"malformed_tool_call_{iteration}_{tc.Index}",
                                tc.FunctionName,
                                tc.ArgumentsJson))
                            .ToList();
                    }
                }

                // If tool calls were requested, execute them and loop
                if (_activeMode?.ToolsEnabled == true && pendingToolCalls.Count > 0 && _toolRegistry is not null)
                {
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

                    AiChatMessage toolCallingAssistantMessage = new(AiChatRole.Assistant, sanitizedResponseContent)
                    {
                        ThinkingContent = sanitizedReasoningContent,
                        ToolCalls = toolCallRequests
                    };
                    AddMessageToHistories(requestConversationHistory, toolCallingAssistantMessage);

                    // ── Phase 1: Create all tool call blocks on the UI thread ──
                    // Each entry holds the tool-call metadata, UI block, and per-tool
                    // cancellation source so we can execute all tools in parallel.
                    List<ToolExecutionItem> executionItems = new(pendingToolCalls.Count);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        for (int i = 0; i < pendingToolCalls.Count; i++)
                        {
                            AiStreamToolCall toolCall = pendingToolCalls[i];
                            string toolCallId = string.IsNullOrWhiteSpace(toolCall.Id)
                                ? $"tool_call_{toolCall.Index}"
                                : toolCall.Id;

                            ToolCallSectionVisual? toolCallBlock = null;
                            CancellationTokenSource? toolCts = null;
                            SpawnAgentInlineContext? spawnContext = null;

                            if (rawTextMode)
                            {
                                string formattedArgs = FormatToolArgs(toolCall.FunctionName, toolCall.ArgumentsJson);
                                if (!rawToolCallBlocks.TryGetValue(toolCall.Index, out TextBlock? rawBlock))
                                {
                                    rawBlock = AppendRawTranscriptEntry(
                                        $"Tool Call ({toolCall.FunctionName})",
                                        formattedArgs,
                                        FindBrush(ThemeResourceKeys.AiChatToolCallForeground),
                                        assistantContainer);
                                    rawToolCallBlocks[toolCall.Index] = rawBlock;
                                }
                                else
                                {
                                    rawBlock.Text = FormatRawTranscriptEntry(
                                        $"Tool Call ({toolCall.FunctionName})", formattedArgs);
                                }
                            }
                            // Create a special spawn_agent inline section with gray background
                            // that streams the sub-agent's conversation directly into the tool area.
                            // Remove any regular tool-call block that may have been created during
                            // streaming (the streaming phase now skips spawn_agent, but guard anyway).
                            else if (string.Equals(toolCall.FunctionName, "spawn_agent", StringComparison.Ordinal))
                            {
                                if (toolCallBlocks.Remove(toolCall.Index, out ToolCallSectionVisual? existingBlock))
                                {
                                    RemoveInlineSection(existingBlock.Section);
                                }

                                string spawnDisplayName = "agent";
                                try
                                {
                                    using JsonDocument? parseDoc = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
                                        ? null
                                        : JsonDocument.Parse(toolCall.ArgumentsJson);
                                    if (parseDoc is not null &&
                                        parseDoc.RootElement.TryGetProperty("displayName", out JsonElement nameEl) &&
                                        nameEl.ValueKind == JsonValueKind.String)
                                    {
                                        spawnDisplayName = nameEl.GetString() ?? "agent";
                                    }
                                    else if (parseDoc is not null &&
                                        parseDoc.RootElement.TryGetProperty("task", out JsonElement taskEl) &&
                                        taskEl.ValueKind == JsonValueKind.String)
                                    {
                                        string? taskPreview = taskEl.GetString();
                                        if (!string.IsNullOrWhiteSpace(taskPreview))
                                        {
                                            spawnDisplayName = taskPreview.Length <= 40
                                                ? taskPreview
                                                : taskPreview[..39] + "…";
                                        }
                                    }
                                }
                                catch { }

                                spawnContext = CreateSpawnAgentSection(
                                    FormatToolCallHeader(toolCall.FunctionName, toolCall.ArgumentsJson),
                                    spawnDisplayName,
                                    assistantContainer,
                                    assistantBlock);

                                // Set up per-tool cancellation
                                toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            }
                            else
                            {
                                if (!toolCallBlocks.TryGetValue(toolCall.Index, out toolCallBlock))
                                {
                                    toolCallBlock = CreateToolCallBlock(
                                        toolCall.FunctionName,
                                        toolCall.ArgumentsJson,
                                        assistantContainer,
                                        assistantBlock);
                                    toolCallBlocks[toolCall.Index] = toolCallBlock;
                                }
                                else
                                {
                                    UpdateToolCallBlock(
                                        toolCallBlock, toolCall.FunctionName, toolCall.ArgumentsJson);
                                }

                                // Set up per-tool cancellation with stop button
                                if (toolCallBlock.Section.StopButton is not null)
                                {
                                    toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                    toolCallBlock.ToolCancellation = toolCts;
                                    Button capturedStopButton = toolCallBlock.Section.StopButton;
                                    capturedStopButton.Click += (_, _) =>
                                    {
                                        try
                                        {
                                            toolCts.Cancel();
                                        }
                                        catch (ObjectDisposedException)
                                        {
                                            // CTS already disposed — ignore
                                        }
                                    };
                                }
                            }

                            executionItems.Add(new ToolExecutionItem(
                                toolCall,
                                toolCallId,
                                i,
                                toolCallBlock,
                                toolCts,
                                rawTextMode
                                    ? rawToolCallBlocks.GetValueOrDefault(toolCall.Index)
                                    : null,
                                spawnContext));
                        }
                    });

                    // ── Phase 2: Execute all tools in parallel ──
                    List<Task<ToolExecutionResult>> toolTasks = new(executionItems.Count);

                    foreach (ToolExecutionItem item in executionItems)
                    {
                        ct.ThrowIfCancellationRequested();

                        CancellationToken effectiveToolCt = item.ToolCancellation?.Token ?? ct;

                        toolTasks.Add(ExecuteToolAndCaptureResultAsync(
                            item, effectiveToolCt, ct));
                    }

                    ToolExecutionResult[] toolResults = await Task.WhenAll(toolTasks)
                        .ConfigureAwait(false);

                    // ── Phase 3: Finalize blocks and add to history (in order) ──
                    await Dispatcher.InvokeAsync(() =>
                    {
                        foreach (ToolExecutionResult execResult in toolResults.OrderBy(r => r.Index))
                        {
                            ToolExecutionItem item = executionItems[execResult.Index];
                            ToolCallResult result = execResult.Result;

                            if (rawTextMode)
                            {
                                AppendRawTranscriptEntry(
                                    $"Tool Result ({item.ToolCall.FunctionName})",
                                    result.Success ? result.Output : result.Error ?? "Unknown error",
                                    result.Success
                                        ? FindBrush(ThemeResourceKeys.AiChatToolCallSuccessForeground)
                                        : FindBrush(ThemeResourceKeys.AiChatToolCallErrorForeground),
                                    assistantContainer);
                            }
                            else if (item.SpawnContext is not null)
                            {
                                // Finalize the inline spawn section with completion status
                                FinalizeSpawnAgentSection(item.SpawnContext, result.Success);
                            }
                            else if (item.ToolCallBlock is not null)
                            {
                                FinalizeToolCallBlock(item.ToolCallBlock, result);
                            }
                        }
                    });

                    // ── Phase 4: Add result messages to history and handle SVG ──
                    foreach (ToolExecutionResult execResult in toolResults.OrderBy(r => r.Index))
                    {
                        ToolExecutionItem item = executionItems[execResult.Index];
                        ToolCallResult result = execResult.Result;

                        string resultContent = result.Success
                            ? result.Output
                            : $"Error: {result.Error}";

                        // If the tool produced SVG content, the provider supports images, and the
                        // user has "Add SVG to context" enabled, render the SVG to a PNG and
                        // attach it as an image on the tool result message so the model can see
                        // it immediately on the next loop iteration.
                        List<AiChatImagePart>? toolMessageImages = null;
                        if (result.Success &&
                            !string.IsNullOrWhiteSpace(result.SvgContent) &&
                            (string.Equals(item.ToolCall.FunctionName, "draw_svg", StringComparison.Ordinal) ||
                             string.Equals(item.ToolCall.FunctionName, "edit_last_svg", StringComparison.Ordinal)))
                        {
                            bool addToContext = _provider?.SupportsImages == true &&
                                SvgToContextCheckBox.IsChecked == true;
                            if (addToContext)
                            {
                                AiChatImagePart? imagePart = RenderSvgToImagePart(result.SvgContent);
                                if (imagePart is not null)
                                {
                                    toolMessageImages = [imagePart];
                                }
                            }
                        }

                        AiChatMessage toolMessage = new(AiChatRole.Tool, resultContent)
                        {
                            ToolCallId = item.ToolCallId,
                            Images = toolMessageImages
                        };
                        AddMessageToHistories(requestConversationHistory, toolMessage);
                    }

                    // If this iteration generated no text content (only thinking/tool calls),
                    // collapse the empty RichTextBox and remove container margins so
                    // consecutive contentless iterations stack flush without gaps.
                    if (string.IsNullOrWhiteSpace(sanitizedResponseContent))
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            assistantBlock.Visibility = Visibility.Collapsed;
                            assistantContainer.Margin = new Thickness(0);
                        });
                    }

                    SavePersistedConversation();

                    // Continue the loop — the next iteration will re-send to the model
                    continue;
                }

                // If this iteration generated no text content, collapse the empty
                // RichTextBox so it doesn't leave a gap in the message panel.
                if (string.IsNullOrWhiteSpace(sanitizedResponseContent))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        assistantBlock.Visibility = Visibility.Collapsed;
                        assistantContainer.Margin = new Thickness(0);
                    });
                }

                // No tool calls — this is the final content response
                AiChatMessage finalAssistantMessage = new(AiChatRole.Assistant, sanitizedResponseContent)
                {
                    ThinkingContent = sanitizedReasoningContent
                };
                AddMessageToHistories(requestConversationHistory, finalAssistantMessage);
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
            _externalContextDirectoryRegistry?.Clear();
            streamStopwatch.Stop();

            await Dispatcher.InvokeAsync(() =>
            {
                _isStreaming = false;

                // Only restore the Send button and update stats if the user isn't
                // viewing a sub-agent. If they are, keep the "◀ Back" button,
                // disabled input, and the agent-specific stats bar.
                if (_viewingAgentId is null)
                {
                    SendButton.Content = "Send";
                    SendButton.IsEnabled = true;
                    SendButton.Click -= StopButton_Click;
                    SendButton.Click += SendButton_Click;

                    UpdateStatsBarFinal(reasoningTokenCount, contentTokenCount, streamStopwatch);
                    RefreshContextWindowDisplay();

                    // If raw mode is enabled, rebuild the display from captured payloads
                    // now that streaming has fully completed and the state is settled.
                    if (IsRawTextModeEnabled() && _rawRequestPayloads.Count > 0)
                    {
                        RebuildConversationDisplay();
                    }
                }
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

    /// <summary>
    /// Returns the last <paramref name="maxLength"/> characters of <paramref name="fullText"/>,
    /// prefixed with "…" if the text was truncated. Newlines are replaced with spaces so the
    /// preview stays on a single line. This is used to show a real-time preview of streaming
    /// content in the section header.
    /// </summary>
    private static string GetStreamingPreview(string? fullText, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(fullText))
        {
            return string.Empty;
        }

        // Flatten newlines so the header stays as a single line
        string flat = fullText
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ");

        if (flat.Length <= maxLength)
        {
            return flat;
        }

        return "…" + flat[^maxLength..];
    }

    private async Task<ToolCallResult> ExecuteSpawnAgentViaOrchestratorAsync(
        string argumentsJson,
        CancellationToken cancellationToken,
        SpawnAgentInlineContext? spawnContext = null)
    {
        if (_agentOrchestrator is null)
        {
            return ToolCallResult.Fail("The multi-agent orchestrator is not initialized.");
        }

        // The root agent is created eagerly by MainWindow.EnsureRootAgent().
        // If it doesn't exist, something went wrong during initialization.
        if (_agentOrchestrator.RootAgent is null)
        {
            return ToolCallResult.Fail("Root agent not initialized. Ensure an AI provider is configured.");
        }

        // Parse arguments to get the task
        string? task = null;

        try
        {
            using JsonDocument? argumentsDocument = string.IsNullOrWhiteSpace(argumentsJson)
                ? null
                : AgentToolArgumentsParser.Parse("spawn_agent", argumentsJson);

            if (argumentsDocument is not null &&
                argumentsDocument.RootElement.TryGetProperty("task", out JsonElement taskElement) &&
                taskElement.ValueKind == JsonValueKind.String)
            {
                task = taskElement.GetString();
            }
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail($"Failed to parse spawn_agent arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return ToolCallResult.Fail("The 'task' parameter is required for spawn_agent.");
        }

        return await ExecuteSpawnAgentInternalAsync(task, argumentsJson, cancellationToken, spawnContext)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Directly spawns and runs a sub-agent via the orchestrator.
    /// </summary>
    private async Task<ToolCallResult> ExecuteSpawnAgentInternalAsync(
        string task,
        string argumentsJson,
        CancellationToken cancellationToken,
        SpawnAgentInlineContext? spawnContext = null)
    {
        if (_agentOrchestrator is null || _agentOrchestrator.RootAgent is null)
        {
            return ToolCallResult.Fail("The multi-agent orchestrator is not ready.");
        }

        IAgent rootAgent = _agentOrchestrator.RootAgent;

        // Parse optional parameters
        string? displayName = null;
        string? providerId = null;
        string? modelOverride = null;
        string? modeId = null;
        string? systemPrompt = null;
        int maxIterations = 50;

        try
        {
            using JsonDocument? document = string.IsNullOrWhiteSpace(argumentsJson)
                ? null
                : AgentToolArgumentsParser.Parse("spawn_agent", argumentsJson);

            if (document is not null)
            {
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("displayName", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    displayName = nameEl.GetString();
                if (root.TryGetProperty("provider", out JsonElement provEl) && provEl.ValueKind == JsonValueKind.String)
                    providerId = provEl.GetString();
                if (root.TryGetProperty("model", out JsonElement modelEl) && modelEl.ValueKind == JsonValueKind.String)
                    modelOverride = modelEl.GetString();
                if (root.TryGetProperty("mode", out JsonElement modeEl) && modeEl.ValueKind == JsonValueKind.String)
                    modeId = modeEl.GetString();
                if (root.TryGetProperty("systemPrompt", out JsonElement promptEl) && promptEl.ValueKind == JsonValueKind.String)
                    systemPrompt = promptEl.GetString();
                if (root.TryGetProperty("maxIterations", out JsonElement iterEl) && iterEl.ValueKind == JsonValueKind.Number)
                    maxIterations = Math.Clamp(iterEl.GetInt32(), 1, 200);
            }
        }
        catch
        {
            // Use defaults if parsing fails
        }

        // Resolve provider
        IAiProvider? provider = rootAgent.Provider;
        if (!string.IsNullOrWhiteSpace(providerId) && _providerRegistry is not null)
        {
            IAiProvider? found = _providerRegistry.Providers
                .FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                provider = found;
            }
        }

        // Resolve mode
        IAiChatMode? mode = rootAgent.Mode;
        if (!string.IsNullOrWhiteSpace(modeId) && _modeRegistry is not null)
        {
            IAiChatMode? found = _modeRegistry.Get(modeId);
            if (found is not null)
            {
                mode = found;
            }
        }

        string effectiveModel = modelOverride ?? rootAgent.Model;
        string effectiveDisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : $"Sub-agent for: {Truncate(task, 60)}";

        IAgent? subAgent = null;

        try
        {
            subAgent = _agentOrchestrator.SpawnSubAgent(
                rootAgent.Id,
                effectiveDisplayName,
                provider,
                effectiveModel,
                mode,
                systemPrompt);

            // Wire up live-update callbacks so the UI can display the sub-agent's
            // progress in real time when the user switches to its view.
            if (subAgent is Services.Ai.Agents.Agent agentImpl)
            {
                // Capture the dispatcher and agent reference for the callbacks.
                // The callbacks are invoked on background threads, so we must
                // dispatch UI updates to the main thread.
                System.Windows.Threading.Dispatcher dispatcher = Dispatcher;
                string agentId = agentImpl.Id;

                agentImpl.IterationCallback = async (_, _) =>
                {
                    // Dispatch a re-render of the agent's messages if the user
                    // is currently viewing this agent. Clear the streaming state
                    // first so the TokenCallback creates fresh UI elements for
                    // the new iteration's streaming response.
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (_viewingAgentId == agentId && _agentOrchestrator?.GetAgent(agentId) is { } currentAgent)
                        {
                            _agentStreamingStates.Remove(agentId);
                            RefreshAgentViewMessages(currentAgent);
                        }
                    });
                };

                // Wire up the token callback so that sub-agent responses are
                // streamed to the UI in real time, matching the behavior of the
                // main chat loop. The Agent.RunAsync method already streams
                // tokens via Provider.StreamCompletionAsync and invokes this
                // callback for each token — we just need to render them.
                // If an inline spawn context is provided, tokens are also
                // streamed into the spawn_agent tool section in the main chat.
                SpawnAgentInlineContext? capturedSpawnContext = spawnContext;
                agentImpl.TokenCallback = (agent, token) =>
                {
                    dispatcher.BeginInvoke(() =>
                    {
                        if (_viewingAgentId == agentId)
                        {
                            RenderAgentStreamToken(agentId, token);
                        }

                        // Stream into the inline spawn section if one exists
                        if (capturedSpawnContext is not null)
                        {
                            HandleSpawnAgentInlineToken(capturedSpawnContext, token);
                        }
                    });

                    return Task.CompletedTask;
                };
            }

            JsonElement toolsDef = subAgent.Mode.ToolsEnabled && _toolRegistry is not null
                ? _toolRegistry.SerializeToolDefinitions(subAgent.Mode.AllowedTools)
                : default;

            AgentRunResult result = await subAgent.RunAsync(
                task,
                toolsDef,
                _toolRegistry ?? new AgentToolRegistry(),
                _agentOrchestrator.FileLockManager,
                _agentOrchestrator,
                maxIterations,
                cancellationToken).ConfigureAwait(false);

            // Dispatch a final refresh so the agent view shows the completed
            // state (the IterationCallback only fires at the start of each
            // iteration, not after the last one). Clear the streaming state
            // so the re-render from _messages is authoritative.
            await Dispatcher.InvokeAsync(() =>
            {
                if (_viewingAgentId == subAgent.Id &&
                    _agentOrchestrator?.GetAgent(subAgent.Id) is { } finalAgent)
                {
                    _agentStreamingStates.Remove(subAgent.Id);
                    RefreshAgentViewMessages(finalAgent);
                }
            });

            // Report back to parent
            rootAgent.ReceiveChildResult(subAgent.Id, result);

            // Release file locks so other agents can edit the same files.
            // The agent remains in the tree so the user can inspect its
            // conversation via the agent session dropdown.
            _agentOrchestrator.FileLockManager.ReleaseAll(subAgent.Id);

            return result.Success
                ? ToolCallResult.Ok($"Sub-agent completed: {result.Summary}")
                : ToolCallResult.Fail($"Sub-agent failed: {result.Summary}");
        }
        catch (OperationCanceledException)
        {
            if (subAgent is not null)
            {
                _agentOrchestrator.FileLockManager.ReleaseAll(subAgent.Id);

                // Refresh the view one last time so the user sees the partial result
                string agentId = subAgent.Id;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_viewingAgentId == agentId &&
                        _agentOrchestrator?.GetAgent(agentId) is { } cancelledAgent)
                    {
                        _agentStreamingStates.Remove(agentId);
                        RefreshAgentViewMessages(cancelledAgent);
                    }
                });
            }

            return ToolCallResult.Fail("Sub-agent execution was cancelled.");
        }
        catch (Exception ex)
        {
            if (subAgent is not null)
            {
                _agentOrchestrator.FileLockManager.ReleaseAll(subAgent.Id);
            }

            return ToolCallResult.Fail($"Sub-agent execution error: {ex.Message}");
        }
    }

    /// <summary>
    /// Holds the per-tool metadata and UI state needed to execute a tool call
    /// in parallel with others during a single iteration.
    /// </summary>
    private sealed class ToolExecutionItem(
        AiStreamToolCall toolCall,
        string toolCallId,
        int index,
        ToolCallSectionVisual? toolCallBlock,
        CancellationTokenSource? toolCancellation,
        TextBlock? rawTextBlock,
        SpawnAgentInlineContext? spawnContext)
    {
        public AiStreamToolCall ToolCall { get; } = toolCall;
        public string ToolCallId { get; } = toolCallId;
        public int Index { get; } = index;
        public ToolCallSectionVisual? ToolCallBlock { get; } = toolCallBlock;
        public CancellationTokenSource? ToolCancellation { get; } = toolCancellation;
        public TextBlock? RawTextBlock { get; } = rawTextBlock;
        public SpawnAgentInlineContext? SpawnContext { get; } = spawnContext;
    }

    /// <summary>
    /// Result of executing a single tool call, captured for parallel execution.
    /// </summary>
    private sealed record ToolExecutionResult(int Index, ToolCallResult Result);

    /// <summary>
    /// Holds the inline streaming state for a spawn_agent tool call so the
    /// sub-agent's conversation is rendered directly inside the tool section
    /// rather than just showing a summary result. The section uses a subtle
    /// gray background to visually distinguish sub-agent sections from
    /// regular tool call sections.
    /// </summary>
    private sealed class SpawnAgentInlineContext
    {
        public StreamSectionVisual Section { get; }
        public ChunkedTextPresenter ResponsePresenter { get; }
        public StringBuilder ResponseBuilder { get; } = new();
        public StringBuilder ReasoningBuilder { get; } = new();
        public StreamSectionVisual? ThinkingSection { get; set; }
        public ChunkedTextPresenter? ThinkingPresenter { get; set; }
        public string DisplayName { get; }
        public int ReasoningTokenCount { get; set; }
        public int ContentTokenCount { get; set; }
        public bool HasContent { get; set; }
        public bool HasThinking { get; set; }

        public SpawnAgentInlineContext(
            StreamSectionVisual section,
            ChunkedTextPresenter responsePresenter,
            string displayName)
        {
            Section = section;
            ResponsePresenter = responsePresenter;
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Executes a single tool call and returns its result. Used by
    /// <see cref="SendMessageAsync"/> to run all tool calls from one iteration
    /// in parallel.
    /// </summary>
    private async Task<ToolExecutionResult> ExecuteToolAndCaptureResultAsync(
        ToolExecutionItem item,
        CancellationToken effectiveToolCt,
        CancellationToken globalCt)
    {
        IAgentTool? tool = IsConversationToolAllowed(item.ToolCall.FunctionName)
            ? _toolRegistry!.Get(item.ToolCall.FunctionName)
            : null;

        ToolCallResult result;

        if (tool is null)
        {
            result = ToolCallResult.Fail($"Unknown or disallowed tool: {item.ToolCall.FunctionName}");
        }
        else if (string.Equals(item.ToolCall.FunctionName, "spawn_agent", StringComparison.Ordinal) &&
                 _agentOrchestrator is not null)
        {
            result = await ExecuteSpawnAgentViaOrchestratorAsync(
                item.ToolCall.ArgumentsJson,
                effectiveToolCt,
                item.SpawnContext).ConfigureAwait(false);
        }
        else if (_agentOrchestrator is not null && GetRootAgentId() is string rootId)
        {
            try
            {
                using JsonDocument? argumentsDocument = string.IsNullOrWhiteSpace(item.ToolCall.ArgumentsJson)
                    ? null
                    : AgentToolArgumentsParser.Parse(item.ToolCall.FunctionName, item.ToolCall.ArgumentsJson);

                JsonElement args = argumentsDocument is null
                    ? default
                    : argumentsDocument.RootElement;

                result = await _agentOrchestrator.ExecuteToolAsync(
                    rootId,
                    item.ToolCall.FunctionName,
                    args,
                    effectiveToolCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (globalCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = ToolCallResult.Fail("Tool execution was cancelled.");
            }
            catch (Exception ex)
            {
                result = ToolCallResult.Fail($"Tool execution error: {ex.Message}");
            }
        }
        else
        {
            try
            {
                using JsonDocument? argumentsDocument = string.IsNullOrWhiteSpace(item.ToolCall.ArgumentsJson)
                    ? null
                    : AgentToolArgumentsParser.Parse(item.ToolCall.FunctionName, item.ToolCall.ArgumentsJson);

                JsonElement args = argumentsDocument is null
                    ? default
                    : argumentsDocument.RootElement;

                result = await tool.ExecuteAsync(args, effectiveToolCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (globalCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = ToolCallResult.Fail("Tool execution was cancelled.");
            }
            catch (Exception ex)
            {
                result = ToolCallResult.Fail($"Tool execution error: {ex.Message}");
            }
        }

        await LogToolFailureAsync(item.ToolCall.FunctionName, item.ToolCallId, item.ToolCall.ArgumentsJson, result);
        return new ToolExecutionResult(item.Index, result);
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

        var modePrompt = GetEffectiveSystemPrompt(toolsDef);

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
    private void UpdateStatsBarFinal(int reasoningTokens, int contentTokens, Stopwatch stopwatch)
    {
        StatsBar.Text = BuildFinalStatsBarText(_aggregatedUsageStats, reasoningTokens, contentTokens, stopwatch.Elapsed);
    }

    internal static string BuildFinalStatsBarText(AiUsageStats? usageStats, int reasoningTokens, int contentTokens, TimeSpan elapsed)
    {
        double elapsedSeconds = elapsed.TotalSeconds;
        int generatedTokens = reasoningTokens + contentTokens;
        List<string> parts = new();

        if (usageStats is not null)
        {
            generatedTokens = usageStats.CompletionTokens;
            parts.Add($"ctx: {usageStats.PromptTokens:N0}");
            parts.Add($"out: {usageStats.CompletionTokens:N0}");
            parts.Add($"total: {usageStats.TotalTokens:N0}");
        }
        else
        {
            if (reasoningTokens > 0)
            {
                parts.Add($"think: {reasoningTokens:N0}");
            }

            parts.Add($"out: {contentTokens:N0}");
        }

        double tokPerSec = elapsedSeconds > 0.1 ? generatedTokens / elapsedSeconds : 0;
        parts.Add($"{tokPerSec:F1} tok/s");
        parts.Add($"{elapsedSeconds:F1}s");

        return string.Join("  •  ", parts);
    }

    internal static AiUsageStats? MergeUsageStats(AiUsageStats? existingUsage, AiUsageStats? nextUsage)
    {
        if (nextUsage is null)
        {
            return existingUsage;
        }

        if (existingUsage is null)
        {
            return nextUsage;
        }

        return new AiUsageStats(
            existingUsage.PromptTokens + nextUsage.PromptTokens,
            existingUsage.CompletionTokens + nextUsage.CompletionTokens,
            existingUsage.TotalTokens + nextUsage.TotalTokens);
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
        AiContextWindowSnapshot snapshot = AiContextWindowBuilder.Build(EnsureActiveConversation().Messages, GetOutboundTokenBudget(), includeToolMessages);
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

    private void RawTextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Avoid rebuilding during active streaming — the inline raw-mode rendering
        // handles live updates. The display will be fully rebuilt once streaming
        // completes or when the user switches conversations.
        if (_isStreaming)
        {
            return;
        }

        // Rebuild the entire conversation display when toggling raw mode.
        // We dispatch to ensure the dispatcher frame has settled.
        Dispatcher.InvokeAsync(RebuildConversationDisplay, DispatcherPriority.Background);
    }

    /// <summary>
    /// Rebuilds the entire <see cref="MessagePanel"/> from the active conversation,
    /// using either raw-mode rendering or normal formatted rendering depending on
    /// the current state of the raw mode checkbox.
    /// </summary>
    private void RebuildConversationDisplay()
    {
        AiConversation conversation = EnsureActiveConversation();
        _streamSections.Clear();
        _inlineImageBorders.Clear();
        _inlineContextSections.Clear();
        PinnedSectionPanel.Children.Clear();
        PinnedSectionPanel.Visibility = Visibility.Collapsed;
        MessagePanel.Children.Clear();

        if (!IsRawTextModeEnabled())
        {
            RebuildNormalConversation(conversation);
        }
        else
        {
            RebuildRawConversation(conversation);
        }

        MessageScroller.ScrollToEnd();
    }

    /// <summary>
    /// Rebuilds the message panel in normal (formatted) mode from the conversation history.
    /// </summary>
    private void RebuildNormalConversation(AiConversation conversation)
    {
        Dictionary<string, ToolCallSectionVisual> toolCallBlocks = new(StringComparer.Ordinal);
        StackPanel? lastAssistantContainer = null;
        RichTextBox? lastAssistantBlock = null;

        // Render all references as inline context sections at the top of the chat
        foreach (AiChatReference reference in conversation.References)
        {
            AppendContextSection(reference);
        }

        foreach (AiChatMessage message in conversation.Messages)
        {
            switch (message.Role)
            {
                case AiChatRole.System:
                    AppendSystemMessage(message.Content);
                    lastAssistantContainer = null;
                    lastAssistantBlock = null;
                    break;
                case AiChatRole.User:
                    AppendUserMessage(message.Content);
                    lastAssistantContainer = null;
                    lastAssistantBlock = null;
                    break;
                case AiChatRole.Assistant:
                    (lastAssistantContainer, lastAssistantBlock) = CreateAssistantMessageBlock();

                    if (!string.IsNullOrWhiteSpace(message.ThinkingContent))
                    {
                        (StreamSectionVisual thinkingSection, ChunkedTextPresenter thinkingPresenter) = CreateThinkingSection(
                            lastAssistantContainer, lastAssistantBlock);
                        thinkingPresenter.ReplaceAll(FormatDisplayedAssistantContent(
                            message.ThinkingContent, ShouldRemoveVerticalWhitespace()));
                        SetInlineSectionHeader(thinkingSection, "Thought");
                    }

                    if (message.ToolCalls is not null)
                    {
                        foreach (AiToolCallRequest toolCall in message.ToolCalls)
                        {
                            ToolCallSectionVisual block = CreateToolCallBlock(
                                toolCall.FunctionName, toolCall.ArgumentsJson,
                                lastAssistantContainer, lastAssistantBlock);
                            toolCallBlocks[toolCall.Id] = block;
                        }
                    }

                    RenderAssistantContent(lastAssistantBlock, message.Content);
                    break;
                case AiChatRole.Tool:
                    if (message.ToolCallId is not null &&
                        toolCallBlocks.TryGetValue(message.ToolCallId, out ToolCallSectionVisual? toolCallBlock))
                    {
                        TryParseToolResult(message.Content, out bool success, out string resultText);
                        ToolCallResult toolCallResult = success
                            ? ToolCallResult.Ok(resultText)
                            : ToolCallResult.Fail(resultText);
                        FinalizeToolCallBlock(toolCallBlock, toolCallResult);
                    }
                    else
                    {
                        AppendSystemMessage($"Tool result: {message.Content}");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Rebuilds the message panel in raw mode, showing the full JSON request
    /// payloads that were sent to the API, plus the captured responses.
    /// When no raw payloads have been captured yet (e.g. a restored conversation),
    /// falls back to rendering the conversation messages as plain text.
    /// </summary>
    private void RebuildRawConversation(AiConversation conversation)
    {
        if (_rawRequestPayloads.Count == 0)
        {
            // No captured payloads — fall back to plain-text rendering of messages
            RenderConversationAsPlainText(conversation);
            return;
        }

        for (int index = 0; index < _rawRequestPayloads.Count; index++)
        {
            RawRequestPayload payload = _rawRequestPayloads[index];
            RenderRawRequestPayload(payload, index);
        }
    }

    /// <summary>
    /// Renders conversation messages as plain monospace text (fallback when
    /// no raw payloads have been captured yet).
    /// </summary>
    private void RenderConversationAsPlainText(AiConversation conversation)
    {
        foreach (AiChatMessage message in conversation.Messages)
        {
            string label = message.Role switch
            {
                AiChatRole.System => "System",
                AiChatRole.User => "User",
                AiChatRole.Assistant => "Assistant",
                AiChatRole.Tool => $"Tool Result ({message.ToolCallId})",
                _ => "Unknown"
            };

            string content = message.Content;

            // Include thinking content if present
            if (!string.IsNullOrWhiteSpace(message.ThinkingContent))
            {
                content = $"(thinking: {message.ThinkingContent})\n\n{content}";
            }

            // Include tool calls if present
            if (message.ToolCalls is { Count: > 0 })
            {
                string toolCallsText = string.Join(
                    "\n",
                    message.ToolCalls.Select(tc => $"  → {tc.FunctionName}({tc.ArgumentsJson})"));
                content = $"{toolCallsText}\n\n{content}";
            }

            TextBlock block = new()
            {
                Text = FormatRawTranscriptEntry(label, content),
                TextWrapping = TextWrapping.NoWrap,
                Foreground = FindBrush("AiChatSecondaryForeground"),
                FontSize = 12,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                Margin = new Thickness(8, 4, 8, 4)
            };

            MessagePanel.Children.Add(block);
        }
    }

    /// <summary>
    /// Pretty-prints a compact JSON string with indentation so lines stay short
    /// and the content is readable without expensive text wrapping.
    /// Returns the original text if it is not valid JSON.
    /// </summary>
    private static string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                doc.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// Renders a single captured raw request payload as a single monospace
    /// <see cref="TextBlock"/> with pretty-printed JSON and no text wrapping,
    /// avoiding the expensive re-measure passes that wrapping large JSON blobs
    /// triggers on every layout change.
    /// </summary>
    private void RenderRawRequestPayload(RawRequestPayload payload, int sequenceNumber)
    {
        // Combine header, model, and pretty-printed JSON into a single string
        // so we only create one TextBlock per payload instead of 3-4.
        string prettyJson = PrettyPrintJson(payload.RequestJson);
        string header = string.IsNullOrWhiteSpace(payload.Model)
            ? $"Request #{sequenceNumber + 1} — {payload.EndpointUrl}"
            : $"Request #{sequenceNumber + 1} — {payload.EndpointUrl}  (model: {payload.Model})";

        string combined = $"{header}\n\n{prettyJson}";

        TextBlock requestBlock = new()
        {
            Text = combined,
            // NoWrap avoids the O(n²) re-measure cost of wrapping multi-line JSON
            // on every layout pass. JSON is already line-broken by pretty-printing,
            // so content is naturally readable without word wrapping.
            TextWrapping = TextWrapping.NoWrap,
            Foreground = FindBrush("AiChatForeground"),
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            Margin = new Thickness(8, 6, 8, 4)
        };
        MessagePanel.Children.Add(requestBlock);
    }

    private bool ShouldAutoExpandThinkingSections()
    {
        return AutoExpandThinkingCheckBox.IsChecked == true;
    }

    private bool ShouldAutoExpandToolSections()
    {
        return AutoExpandToolsCheckBox.IsChecked == true;
    }

    private bool ShouldAutoExpandContextSections()
    {
        return AutoExpandContextCheckBox.IsChecked == true;
    }

    private bool ShouldAutoExpandImagesSections()
    {
        // When auto expand context is on, images are already included.
        // This checkbox is only effective when auto expand context is off,
        // and defaults to true.
        return !ShouldAutoExpandContextSections()
            && AutoExpandImagesCheckBox.IsChecked == true;
    }

    private void AutoExpandContextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool contextExpanded = AutoExpandContextCheckBox.IsChecked == true;
        AutoExpandImagesCheckBox.IsEnabled = !contextExpanded;
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
    /// Uses a <see cref="ChunkedTextPresenter"/> so that only a small, bounded
    /// TextBlock is updated on each streaming token.
    /// </summary>
    private (StreamSectionVisual section, ChunkedTextPresenter presenter) CreateThinkingSection(Panel hostPanel, UIElement insertBefore)
    {
        Brush thinkingBackground = FindBrush("AiChatThinkingBackground");
        Brush thinkingContentBackground = FindBrush("AiChatThinkingContentBackground");
        Brush thinkingForeground = FindBrush("AiChatThinkingForeground");
        Brush thinkingBorder = FindBrush("AiChatThinkingBorder");
        Brush streamForeground = FindBrush(ThemeResourceKeys.AiChatStreamingContentForeground);
        StreamSectionVisual section = CreateInlineSection(
            "Thinking...",
            thinkingBackground,
            thinkingContentBackground,
            thinkingForeground,
            thinkingBorder,
            hostPanel,
            insertBefore,
            streamingContentForeground: streamForeground);

        var presenter = new ChunkedTextPresenter(
            section.ContentPanel,
            thinkingForeground,
            new FontFamily("Segoe UI"),
            12);

        SetInlineSectionExpanded(section, isExpanded: ShouldAutoExpandThinkingSections());
        return (section, presenter);
    }

    /// <summary>
    /// Creates a collapsible context section in the message panel showing the content
    /// of an attached reference (file, document, build output, etc.). Uses a subtle
    /// blue tint to distinguish context from thinking (warm) and tool calls (yellow).
    /// </summary>
    private void AppendContextSection(AiChatReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        Brush contextBackground = FindBrush(ThemeResourceKeys.AiChatContextBackground);
        Brush contextContentBackground = FindBrush(ThemeResourceKeys.AiChatContextContentBackground);
        Brush contextForeground = FindBrush(ThemeResourceKeys.AiChatContextForeground);
        Brush contextBorder = FindBrush(ThemeResourceKeys.AiChatContextBorder);

        string header = GetContextSectionHeader(reference);
        string content = GetContextSectionContent(reference);

        StreamSectionVisual section = CreateInlineSection(
            header,
            contextBackground,
            contextContentBackground,
            contextForeground,
            contextBorder,
            MessagePanel,
            insertBefore: null);

        // Add a dismiss (✕) button to the far right of the header so the user can
        // remove the context item directly from the chat without using the tag bar.
        if (section.HeaderBar.Child is Grid headerGrid)
        {
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button dismissButton = new()
            {
                Content = "✕",
                FontSize = 11,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 20,
                MinHeight = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Foreground = contextForeground,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "Remove this context item"
            };

            // Capture the reference for the click handler
            AiChatReference capturedReference = reference;
            dismissButton.Click += (_, _) => RemoveReference(capturedReference);

            Grid.SetColumn(dismissButton, headerGrid.ColumnDefinitions.Count - 1);
            headerGrid.Children.Add(dismissButton);
        }

        // Render the content as a plain text block (context is pre-rendered, not streamed).
        // For image references, render the image thumbnail instead.
        if (reference.Kind == AiReferenceKind.Image)
        {
            AppendContextImageContent(reference, section);
        }
        else if (!string.IsNullOrWhiteSpace(content))
        {
            TextBlock contentBlock = new()
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                Foreground = contextForeground,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI")
            };
            section.ContentPanel.Children.Add(contentBlock);
        }

        bool autoExpand = reference.Kind == AiReferenceKind.Image
            ? (ShouldAutoExpandContextSections() || ShouldAutoExpandImagesSections())
            : ShouldAutoExpandContextSections();
        SetInlineSectionExpanded(section, isExpanded: autoExpand);
        _inlineContextSections[reference] = section;
        MessageScroller.ScrollToEnd();
    }

    /// <summary>
    /// Returns a human-readable header string for the context section based on the reference kind.
    /// </summary>
    private static string GetContextSectionHeader(AiChatReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        string icon = reference.Kind switch
        {
            AiReferenceKind.File => "📄",
            AiReferenceKind.CurrentDocument => "📝",
            AiReferenceKind.OpenDocuments => "🗂️",
            AiReferenceKind.BuildOutput => "🏗️",
            AiReferenceKind.Class => "🔷",
            AiReferenceKind.ExternalFolder => "📁",
            AiReferenceKind.Url => "🔗",
            AiReferenceKind.Image => "🖼️",
            _ => "📎"
        };

        string kindLabel = reference.Kind switch
        {
            AiReferenceKind.File => "File",
            AiReferenceKind.CurrentDocument => "Current Document",
            AiReferenceKind.OpenDocuments => "Open Documents",
            AiReferenceKind.BuildOutput => "Build Output",
            AiReferenceKind.Class => "Class",
            AiReferenceKind.ExternalFolder => "External Folder",
            AiReferenceKind.Url => "URL",
            AiReferenceKind.Image => "Image",
            _ => "Context"
        };

        return $"{icon} {kindLabel}: {reference.DisplayName}";
    }

    /// <summary>
    /// Returns the formatted content for the context section body.
    /// For most kinds this is the <see cref="AiChatReference.Content"/> directly.
    /// </summary>
    private static string GetContextSectionContent(AiChatReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (string.IsNullOrWhiteSpace(reference.Content))
        {
            return string.Empty;
        }

        // For OpenDocuments, the content is already a formatted summary
        // For everything else, just return the content as-is
        return reference.Content;
    }

    /// <summary>
    /// Renders an image thumbnail inside a context section's content panel.
    /// The image is clickable to open a full-screen viewer, and a filename
    /// info bar is shown below the thumbnail.
    /// </summary>
    private void AppendContextImageContent(AiChatReference reference, StreamSectionVisual section)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(section);

        if (!File.Exists(reference.FullPath))
        {
            TextBlock missingBlock = new()
            {
                Text = "(image file not found)",
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Foreground = FindBrush(ThemeResourceKeys.AiChatContextForeground),
                FontSize = 12
            };
            section.ContentPanel.Children.Add(missingBlock);
            return;
        }

        try
        {
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(reference.FullPath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 780;
            bitmap.EndInit();
            bitmap.Freeze();

            string capturedFilePath = reference.FullPath;
            string capturedDisplayName = reference.DisplayName;

            Image image = new()
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = 780,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                ToolTip = "Click to enlarge"
            };

            image.MouseLeftButtonDown += (_, _) =>
            {
                try
                {
                    BitmapImage viewerBitmap = new();
                    viewerBitmap.BeginInit();
                    viewerBitmap.UriSource = new Uri(capturedFilePath);
                    viewerBitmap.CacheOption = BitmapCacheOption.OnLoad;
                    viewerBitmap.EndInit();
                    viewerBitmap.Freeze();

                    ImageViewerWindow.Open(viewerBitmap, capturedDisplayName, Window.GetWindow(this));
                }
                catch (Exception)
                {
                    // Silently skip if the high-res load fails
                }
            };

            TextBlock infoBar = new()
            {
                Text = $"🖼️ {reference.DisplayName}",
                FontSize = 11,
                Foreground = FindBrush("AiChatSecondaryForeground"),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            section.ContentPanel.Children.Add(image);
            section.ContentPanel.Children.Add(infoBar);
        }
        catch (Exception)
        {
            TextBlock errorBlock = new()
            {
                Text = "(could not load image)",
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Foreground = FindBrush(ThemeResourceKeys.AiChatContextForeground),
                FontSize = 12
            };
            section.ContentPanel.Children.Add(errorBlock);
        }
    }

    private StreamSectionVisual CreateInlineSection(
        string header,
        Brush headerBackground,
        Brush contentBackground,
        Brush foreground,
        Brush borderBrush,
        Panel hostPanel,
        UIElement? insertBefore,
        Brush? streamingContentForeground = null,
        UIElement? stopButton = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        ArgumentNullException.ThrowIfNull(headerBackground);
        ArgumentNullException.ThrowIfNull(contentBackground);
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(borderBrush);
        ArgumentNullException.ThrowIfNull(hostPanel);

        TextBlock glyphBlock;
        TextBlock headerTextBlock;
        TextBlock? streamContentBlock;
        Border headerBar = CreateSectionHeaderBar(
            header, foreground, headerBackground, borderBrush,
            out glyphBlock, out headerTextBlock, out streamContentBlock,
            stopButton: stopButton);

        // Apply streaming content foreground if provided
        if (streamContentBlock is not null && streamingContentForeground is not null)
        {
            streamContentBlock.Foreground = streamingContentForeground;
        }

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
            Margin = new Thickness(0),
            Child = sectionLayout
        };

        StreamSectionVisual section = new(
            root,
            headerBar,
            glyphBlock,
            headerTextBlock,
            streamContentBlock,
            contentBorder,
            contentPanel,
            headerBackground,
            contentBackground,
            foreground,
            borderBrush,
            stopButton: stopButton as Button);

        headerBar.MouseLeftButtonUp += (_, _) => ToggleInlineSection(section);
        InsertBefore(hostPanel, root, insertBefore);
        _streamSections.Add(section);
        UpdateInlineSectionState(section);
        return section;
    }

    /// <summary>
    /// Creates a section header bar with a layout:
    ///   [glyph (Auto)] [title (Auto-sized)] [streaming content preview (*, right-aligned)] [stopButton (Auto)]
    ///
    /// The streaming content column is always created but initially empty.
    /// An optional stop button can be provided for long-running tools.
    /// </summary>
    private static Border CreateSectionHeaderBar(
        string title,
        Brush foreground,
        Brush background,
        Brush borderBrush,
        out TextBlock glyphBlock,
        out TextBlock titleBlock,
        out TextBlock? streamContentBlock,
        UIElement? stopButton = null)
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
            Foreground = foreground,
            Margin = new Thickness(0, 0, 16, 0)
        };

        // Streaming content preview column (right-aligned, fills remaining width).
        // Wrapped in a Border with ClipToBounds so the text is anchored to the right
        // and any overflow past the left edge is clipped.
        streamContentBlock = new TextBlock
        {
            Text = "",
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 8,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = foreground,
            Opacity = 0.75
        };

        Border streamClipContainer = new()
        {
            ClipToBounds = true,
            Child = streamContentBlock,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Build grid: [glyph (Auto)] [title (Auto)] [streaming (*)] [stopButton (Auto)]
        Grid headerGrid = new();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: glyph
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: title
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2: streaming content
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3: stop button (optional)

        Grid.SetColumn(glyphBlock, 0);
        headerGrid.Children.Add(glyphBlock);

        Grid.SetColumn(titleBlock, 1);
        headerGrid.Children.Add(titleBlock);

        Grid.SetColumn(streamClipContainer, 2);
        headerGrid.Children.Add(streamClipContainer);

        if (stopButton is not null)
        {
            Grid.SetColumn(stopButton, 3);
            headerGrid.Children.Add(stopButton);
        }

        return new Border
        {
            Background = background,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            Cursor = Cursors.Hand,
            Child = headerGrid
        };
    }

    private static void InsertBefore(Panel hostPanel, UIElement element, UIElement? insertBefore)
    {
        ArgumentNullException.ThrowIfNull(hostPanel);
        ArgumentNullException.ThrowIfNull(element);

        if (insertBefore is not null)
        {
            int index = hostPanel.Children.IndexOf(insertBefore);
            if (index >= 0)
            {
                hostPanel.Children.Insert(index, element);
                return;
            }
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
        SetInlineSectionHeaderCore(section, header, updatePinnedSections: true);
    }

    /// <summary>
    /// Sets the section header without triggering a pinned-section layout pass.
    /// Use during rapid streaming updates where pinned headers will be refreshed
    /// in a batching call after the streaming burst.
    /// </summary>
    private void SetInlineSectionHeaderNoPinnedUpdate(StreamSectionVisual section, string header)
    {
        SetInlineSectionHeaderCore(section, header, updatePinnedSections: false);
    }

    private void SetInlineSectionHeaderCore(StreamSectionVisual section, string header, bool updatePinnedSections)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        section.HeaderText.Text = header;
        if (updatePinnedSections)
        {
            UpdatePinnedSectionHeaders();
        }
    }

    private void SetInlineSectionForeground(StreamSectionVisual section, Brush foreground)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(foreground);

        section.Foreground = foreground;
        section.HeaderGlyph.Foreground = foreground;
        section.HeaderText.Foreground = foreground;
        UpdatePinnedSectionHeaders();
    }

    /// <summary>
    /// Updates the streaming content preview in the section header, showing the last
    /// ~200 characters of the currently streamed content. Only updates when the section
    /// is collapsed; when expanded the body content is visible instead.
    /// </summary>
    private static void UpdateStreamingContentPreview(StreamSectionVisual section, string? fullContent)
    {
        if (section.HeaderStreamContent is null)
        {
            return;
        }

        section.HeaderStreamContent.Text = GetStreamingPreview(fullContent);
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

        TextBox tb = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            Foreground = FindBrush("AiChatSecondaryForeground"),
            FontSize = 12,
            Margin = new Thickness(8, 4, 8, 4),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsInactiveSelectionHighlightEnabled = true,
            Focusable = true
        };

        // Suppress the default focus rectangle so selected text is the only visual cue
        tb.FocusVisualStyle = null;

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
    /// Creates a collapsible tool-call block with a tool-title header, chunked arguments
    /// content, and result content. Uses <see cref="ChunkedTextPresenter"/> so that only
    /// a small, bounded TextBlock is updated on each streaming token.
    /// </summary>
    private ToolCallSectionVisual CreateToolCallBlock(
        string toolName,
        string argumentsJson,
        Panel hostPanel,
        UIElement insertBefore)
    {
        Brush toolBackground = FindBrush(ThemeResourceKeys.AiChatToolCallBackground);
        Brush toolContentBackground = FindBrush(ThemeResourceKeys.AiChatToolCallContentBackground);
        Brush toolForeground = FindBrush(ThemeResourceKeys.AiChatToolCallForeground);
        Brush toolBorder = FindBrush(ThemeResourceKeys.AiChatToolCallBorder);
        Brush streamForeground = FindBrush(ThemeResourceKeys.AiChatStreamingContentForeground);
        var monoFont = new FontFamily("Cascadia Code, Consolas, Courier New");

        string headerText = FormatToolCallHeader(toolName, argumentsJson);

        // Create a stop button for long-running tools like "run".
        // The click handler is wired later (in the tool-execution loop) to cancel
        // just this tool's per-tool CancellationTokenSource, not the global stream.
        Button? stopButton = null;
        if (string.Equals(toolName, "run", StringComparison.Ordinal))
        {
            stopButton = new Button
            {
                Content = "⏹",
                FontSize = 11,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 20,
                MinHeight = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Foreground = toolForeground,
                Background = toolBackground,
                BorderBrush = toolBorder,
                BorderThickness = new Thickness(1),
                ToolTip = "Stop running program"
            };
        }

        StreamSectionVisual section = CreateInlineSection(
            headerText,
            toolBackground,
            toolContentBackground,
            toolForeground,
            toolBorder,
            hostPanel,
            insertBefore,
            streamingContentForeground: streamForeground,
            stopButton: stopButton);

        /* The result block goes after the argument chunks. Track the index so the
         * ChunkedTextPresenter inserts argument blocks before it. */
        TextBlock resultBlock = new()
        {
            Text = "Receiving arguments...",
            TextWrapping = TextWrapping.Wrap,
            Foreground = toolForeground,
            FontSize = 11,
            FontFamily = monoFont
        };

        section.ContentPanel.Children.Add(resultBlock);

        var argumentsPresenter = new ChunkedTextPresenter(
            section.ContentPanel,
            toolForeground,
            monoFont,
            11,
            insertBeforeElement: resultBlock /* Insert argument chunks before the result block */);

        SetInlineSectionExpanded(section, isExpanded: ShouldAutoExpandToolSections());

        bool shouldStickToBottom = IsMessageScrollerNearBottom();
        if (shouldStickToBottom)
        {
            MessageScroller.ScrollToEnd();
        }

        return new ToolCallSectionVisual(section, argumentsPresenter, resultBlock, toolName, argumentsJson);
    }

    /// <summary>
    /// Creates a section for a spawn_agent tool call with a subtle gray
    /// background and a content area that will stream the sub-agent's
    /// conversation inline. Unlike a regular tool call block, this section
    /// shows the sub-agent's live response text rather than a static result.
    /// </summary>
    private SpawnAgentInlineContext CreateSpawnAgentSection(
        string headerText,
        string displayName,
        Panel hostPanel,
        UIElement insertBefore)
    {
        // Use subtle gray tones distinct from the warm tool-call colors
        Brush spawnHeaderBg = FindBrush("AiChatHeaderBackground");
        Brush spawnContentBg = FindBrush("AiChatInputBackground");
        Brush spawnForeground = FindBrush("AiChatForeground");
        Brush spawnBorder = FindBrush("AiChatBorder");
        Brush streamForeground = FindBrush("AiChatSecondaryForeground");
        FontFamily monoFont = new("Cascadia Code, Consolas, Courier New");

        StreamSectionVisual section = CreateInlineSection(
            headerText,
            spawnHeaderBg,
            spawnContentBg,
            spawnForeground,
            spawnBorder,
            hostPanel,
            insertBefore,
            streamingContentForeground: streamForeground,
            stopButton: null);

        // Give the spawn section a fixed viewing window so the sub-agent's
        // streaming conversation doesn't take over the main chat vertically
        const double spawnSectionMaxHeight = 260;
        section.ContentBorder.MaxHeight = spawnSectionMaxHeight;

        // Wrap the content panel in a ScrollViewer so overflow content can be
        // scrolled when the sub-agent's response exceeds the fixed height
        section.ContentBorder.Child = null;
        ScrollViewer scrollViewer = new()
        {
            Content = section.ContentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        section.ContentBorder.Child = scrollViewer;

        // Response block for streaming the sub-agent's reply text
        TextBlock responsePlaceholder = new()
        {
            Text = "Starting sub-agent...",
            TextWrapping = TextWrapping.Wrap,
            Foreground = spawnForeground,
            FontSize = 11,
            FontFamily = monoFont
        };

        section.ContentPanel.Children.Add(responsePlaceholder);

        ChunkedTextPresenter responsePresenter = new(
            section.ContentPanel,
            spawnForeground,
            monoFont,
            11,
            insertBeforeElement: null);

        // Replace the placeholder with the first chunk immediately
        responsePlaceholder.Text = string.Empty;
        responsePresenter.Append(string.Empty);

        SetInlineSectionExpanded(section, isExpanded: true);
        SetInlineSectionHeader(section, $"Sub-agent: {displayName}");

        return new SpawnAgentInlineContext(section, responsePresenter, displayName);
    }

    /// <summary>
    /// Updates a tool-call block while the call arguments are still streaming.
    /// Appends only the delta (new characters not yet rendered) to the chunked
    /// presenter, so only the current small TextBlock is updated.
    /// Also updates the header streaming content preview.
    /// </summary>
    private void UpdateToolCallBlock(ToolCallSectionVisual block, string toolName, string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(block);

        block.Section.HeaderText.Text = FormatToolCallHeader(toolName, argumentsJson);

        UpdateStreamingContentPreview(block.Section, argumentsJson);

        /* During streaming, each token carries the FULL accumulated argumentsJson.
         * Compute the delta — only append characters not yet shown. */
        int alreadyShown = block.ArgumentsPresenter.ApproximateLength;
        if (alreadyShown < argumentsJson.Length)
        {
            block.ArgumentsPresenter.Append(argumentsJson[alreadyShown..]);
        }
    }

    /// <summary>
    /// Finalizes a tool-call block with the execution result.
    /// At this point streaming is complete, so we safely format the header
    /// and arguments content once using proper JSON parsing, and set the
    /// result output.
    /// </summary>
    private void FinalizeToolCallBlock(ToolCallSectionVisual block, ToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(block);

        /* The header was already set correctly during streaming (or creation) via
         * FormatToolCallHeader, which includes the file path when available. We keep it
         * as-is — the only change at finalize time is the foreground color below. */

        /* Replace the chunked streaming content with properly formatted final content.
         * This is a single JSON parse and a single batch of TextBlock creations,
         * regardless of how many streaming tokens were processed. */
        string formattedArgs = FormatToolCallBody(block.ToolName, block.ArgumentsJson);
        if (!string.IsNullOrWhiteSpace(formattedArgs))
        {
            block.ArgumentsPresenter.ReplaceAll(formattedArgs);
        }
        else
        {
            block.ArgumentsPresenter.Clear();
        }

        if (result.Success)
        {
            Brush successForeground = FindBrush(GetToolCallHeaderForegroundKey(success: true));
            block.ResultBlock.Text = result.Output;
            block.ResultBlock.Foreground = successForeground;
            SetInlineSectionForeground(block.Section, successForeground);
        }
        else
        {
            Brush errorForeground = FindBrush(GetToolCallHeaderForegroundKey(success: false));
            block.ResultBlock.Text = result.Error ?? "Unknown error";
            block.ResultBlock.Foreground = errorForeground;
            SetInlineSectionForeground(block.Section, errorForeground);
        }

        // Hide the stop button (if any) since the tool call has completed
        if (block.Section.StopButton is not null)
        {
            block.Section.StopButton.Visibility = Visibility.Collapsed;
        }

        // Dispose the per-tool cancellation source (if any)
        if (block.ToolCancellation is not null)
        {
            block.ToolCancellation.Dispose();
            block.ToolCancellation = null;
        }

        // Render SVG inline if present (draw_svg or edit_last_svg tools)
        if (result.Success && !string.IsNullOrWhiteSpace(result.SvgContent) &&
            (string.Equals(block.ToolName, "draw_svg", StringComparison.Ordinal) ||
             string.Equals(block.ToolName, "edit_last_svg", StringComparison.Ordinal)))
        {
            // If the provider supports images and the "Add SVG to context" checkbox
            // is enabled, add the rendered SVG as a vision reference so the model
            // can "see" the result in subsequent turns. In that case the context
            // section itself shows the image, so skip the inline preview to avoid
            // showing the image twice.
            bool shouldAddToContext = _provider?.SupportsImages == true
                && SvgToContextCheckBox.IsChecked == true;

            if (!shouldAddToContext)
            {
                AppendSvgImage(block.Section.Root, result.SvgContent);
            }

            if (shouldAddToContext)
            {
                TryAddSvgAsImageReference(result.SvgContent);
            }
        }

        bool shouldStickToBottom = IsMessageScrollerNearBottom();

        if (shouldStickToBottom)
        {
            MessageScroller.ScrollToEnd();
        }
    }

    /// <summary>
    /// Renders a pasted/copied image inline in the message panel, similar to how
    /// <see cref="draw_svg"/> results appear. The image includes a remove button
    /// and a click-to-enlarge handler. This replaces the old reference-tag-only
    /// approach for images — images now appear inline in the chat flow.
    /// </summary>
    private void AppendInlineImage(AiChatReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.Kind != AiReferenceKind.Image || string.IsNullOrWhiteSpace(reference.FullPath))
        {
            return;
        }

        if (!File.Exists(reference.FullPath))
        {
            return;
        }

        try
        {
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(reference.FullPath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 780;
            bitmap.EndInit();
            bitmap.Freeze();

            // Remove button in the top-right corner of the inline image
            Button removeButton = new()
            {
                Content = "✕",
                FontSize = 11,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Foreground = FindBrush("AiChatRefTagForeground"),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x40, 0x40, 0x40)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Remove image",
                Margin = new Thickness(0, 2, 2, 0),
                Tag = reference
            };
            removeButton.Click += (_, _) =>
            {
                if (removeButton.Tag is AiChatReference refToRemove)
                {
                    RemoveReference(refToRemove);
                }
            };

            Image image = new()
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = 780,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = "Click to enlarge"
            };

            // Capture for the click handler
            string capturedFilePath = reference.FullPath;
            string capturedDisplayName = reference.DisplayName;
            image.MouseLeftButtonDown += (_, _) =>
            {
                try
                {
                    BitmapImage viewerBitmap = new();
                    viewerBitmap.BeginInit();
                    viewerBitmap.UriSource = new Uri(capturedFilePath);
                    viewerBitmap.CacheOption = BitmapCacheOption.OnLoad;
                    viewerBitmap.EndInit();
                    viewerBitmap.Freeze();

                    ImageViewerWindow.Open(viewerBitmap, capturedDisplayName, Window.GetWindow(this));
                }
                catch (Exception)
                {
                    // Silently skip if the high-res load fails
                }
            };

            // Info bar showing the image filename
            TextBlock infoBar = new()
            {
                Text = $"🖼️ {reference.DisplayName}",
                FontSize = 11,
                Foreground = FindBrush("AiChatSecondaryForeground"),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // Wrap image + info in a vertically-oriented panel
            StackPanel imagePanel = new()
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(12, 0, 12, 0),
                Children =
                {
                    image,
                    infoBar
                }
            };

            // Wrap everything in a border with the remove button overlaid
            Grid overlayGrid = new();
            overlayGrid.Children.Add(imagePanel);
            overlayGrid.Children.Add(removeButton);

            Border border = new()
            {
                Child = overlayGrid,
                Background = FindBrush("AiChatInputBackground"),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(40, 4, 8, 4),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = reference
            };

            _inlineImageBorders[reference] = border;
            MessagePanel.Children.Add(border);
            MessageScroller.ScrollToEnd();
        }
        catch (IOException)
        {
            // Image file is missing or inaccessible — skip inline rendering
        }
        catch (NotSupportedException)
        {
            // Image format is not supported — skip inline rendering
        }
    }

    /// <summary>
    /// Renders an SVG image below the given tool call section root element.
    /// The image is displayed directly in the message panel, visible even when
    /// the tool call expander is collapsed. Clicking the image opens a full-screen
    /// viewer with zoom and pan support.
    /// </summary>
    private void AppendSvgImage(Border sectionRoot, string svgContent)
    {
        ArgumentNullException.ThrowIfNull(sectionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(svgContent);

        if (sectionRoot.Parent is not Panel parentPanel)
        {
            return;
        }

        try
        {
            using var svgStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgContent));
            Svg.SvgDocument svgDoc = Svg.SvgDocument.Open<Svg.SvgDocument>(svgStream);

            // Render at a reasonable display width (fit within chat panel)
            using System.Drawing.Bitmap bitmap = svgDoc.Draw(800, 0);

            BitmapSource bitmapSource = ConvertBitmapToBitmapSource(bitmap);

            Image image = new()
            {
                Source = bitmapSource,
                Stretch = Stretch.Uniform,
                MaxWidth = 780,
                Margin = new Thickness(8, 8, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = "Click to enlarge"
            };

            // Capture the SVG content so it can be saved and re-rendered at high resolution
            string capturedSvg = svgContent;

            // Add right-click context menu with Save As option
            ContextMenu contextMenu = new();
            MenuItem saveMenuItem = new()
            {
                Header = "Save As..."
            };

            saveMenuItem.Click += (sender, _) =>
            {
                try
                {
                    Microsoft.Win32.SaveFileDialog saveDialog = new()
                    {
                        Filter = "SVG Files (*.svg)|*.svg|All Files (*.*)|*.*",
                        DefaultExt = "svg",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        Title = "Save SVG Image"
                    };

                    if (saveDialog.ShowDialog() == true)
                    {
                        File.WriteAllText(saveDialog.FileName, capturedSvg, System.Text.Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save SVG: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            contextMenu.Items.Add(saveMenuItem);
            image.ContextMenu = contextMenu;
            image.MouseLeftButtonDown += (_, _) =>
            {
                try
                {
                    using var viewerStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(capturedSvg));
                    Svg.SvgDocument viewerDoc = Svg.SvgDocument.Open<Svg.SvgDocument>(viewerStream);

                    // Render at higher resolution for the full-screen viewer
                    using System.Drawing.Bitmap viewerBitmap = viewerDoc.Draw(1600, 0);
                    BitmapSource viewerSource = ConvertBitmapToBitmapSource(viewerBitmap);

                    ImageViewerWindow.Open(viewerSource, "SVG Image", Window.GetWindow(this));
                }
                catch (Exception)
                {
                    // Fallback: open with the inline-resolution bitmap if re-render fails
                    ImageViewerWindow.Open(bitmapSource, "SVG Image", Window.GetWindow(this));
                }
            };

            // Insert the image after the section root in the parent panel
            int sectionIndex = parentPanel.Children.IndexOf(sectionRoot);
            if (sectionIndex >= 0 && sectionIndex < parentPanel.Children.Count - 1)
            {
                parentPanel.Children.Insert(sectionIndex + 1, image);
            }
            else
            {
                parentPanel.Children.Add(image);
            }
        }
        catch (Exception)
        {
            // SVG rendering failed — the text result is already shown,
            // so we silently skip the inline image.
        }
    }

    /// <summary>
    /// Converts a <see cref="System.Drawing.Bitmap"/> to a WPF <see cref="BitmapSource"/>,
    /// properly freeing the GDI handle after conversion.
    /// </summary>
    private static BitmapSource ConvertBitmapToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        IntPtr hBitmap = bitmap.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);
    }

    /// <summary>
    /// Renders SVG content to a PNG, returning the base64-encoded image data
    /// and MIME type suitable for attaching to an <see cref="AiChatMessage.Images"/>
    /// collection. Returns null if rendering fails.
    /// </summary>
    private static AiChatImagePart? RenderSvgToImagePart(string svgContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(svgContent);

        try
        {
            using MemoryStream svgStream = new(System.Text.Encoding.UTF8.GetBytes(svgContent));
            Svg.SvgDocument svgDoc = Svg.SvgDocument.Open<Svg.SvgDocument>(svgStream);

            using System.Drawing.Bitmap bitmap = svgDoc.Draw(800, 0);
            using MemoryStream pngStream = new();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            byte[] pngBytes = pngStream.ToArray();

            return new AiChatImagePart(Convert.ToBase64String(pngBytes), "image/png");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Renders the given SVG content to a PNG file, saves it to the pasted-images
    /// directory, and adds it as an <see cref="AiReferenceKind.Image"/> reference
    /// so the AI model can "see" the rendered visual output in subsequent turns.
    /// </summary>
    private void TryAddSvgAsImageReference(string svgContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(svgContent);

        try
        {
            AiChatImagePart? imagePart = RenderSvgToImagePart(svgContent);
            if (imagePart is null)
            {
                return;
            }

            byte[] pngBytes = Convert.FromBase64String(imagePart.Base64Data);
            string filePath = SavePastedImage(pngBytes, ".png");
            AiChatReference reference = new(AiReferenceKind.Image, filePath, "SVG rendering");

            try
            {
                byte[] imageBytes = File.ReadAllBytes(filePath);
                reference.Content = Convert.ToBase64String(imageBytes);
            }
            catch (IOException)
            {
                reference.Content = string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                reference.Content = string.Empty;
            }

            // Use AddReference so the image appears as a proper blue-tinted
            // context section (collapsible, with pinned header support) instead
            // of a bare inline image.
            AddReference(reference);
        }
        catch (Exception)
        {
            // Rendering or saving failed — the SVG is already shown inline,
            // so we silently skip adding it as context.
        }
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

    /// <summary>
    /// Formats tool arguments JSON into a readable display string.
    /// </summary>
    private static string FormatToolArgs(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "(no arguments)";
        }

        try
        {
            using JsonDocument doc = AgentToolArgumentsParser.Parse(toolName, argumentsJson);
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

    internal static string FormatToolCallBody(string toolName, string argumentsJson)
    {
        return string.IsNullOrWhiteSpace(argumentsJson)
            ? string.Empty
            : FormatToolArgs(toolName, argumentsJson);
    }

    internal static string GetVisibleAssistantContent(string content, bool toolsEnabled)
    {
        ArgumentNullException.ThrowIfNull(content);

        return toolsEnabled
            ? MalformedToolCallRecovery.StripToolCallMarkup(content)
            : content;
    }

    internal static string FormatToolCallHeader(string toolName, string? argumentsJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        string header = toolName;
        string? argumentSummary = TryFormatToolCallHeaderArgumentSummary(toolName, argumentsJson);

        if (!string.IsNullOrWhiteSpace(argumentSummary))
        {
            header = $"{toolName} - {argumentSummary}";
        }

        return Truncate(header, 180);
    }

    internal static string GetToolCallHeaderForegroundKey(bool success)
    {
        return success
            ? ThemeResourceKeys.AiChatToolCallSuccessForeground
            : ThemeResourceKeys.AiChatToolCallErrorForeground;
    }

    private static string? TryFormatToolCallHeaderArgumentSummary(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = AgentToolArgumentsParser.Parse(toolName, argumentsJson);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetToolHeaderPathArgument(root, "sourcePath", out string? sourcePath) &&
                TryGetToolHeaderPathArgument(root, "destinationPath", out string? destinationPath))
            {
                return $"{Path.GetFileName(sourcePath)} -> {Path.GetFileName(destinationPath)}";
            }

            if (TryGetToolHeaderPathArgument(root, "filePath", out string? filePath))
            {
                return Path.GetFileName(filePath);
            }

            if (root.TryGetProperty("filePaths", out JsonElement filePathsElement) &&
                filePathsElement.ValueKind == JsonValueKind.Array &&
                filePathsElement.GetArrayLength() > 0)
            {
                JsonElement firstPathElement = filePathsElement[0];
                if (firstPathElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(firstPathElement.GetString()))
                {
                    return null;
                }

                string firstPath = Path.GetFileName(firstPathElement.GetString()!.Trim());
                int remainingFileCount = filePathsElement.GetArrayLength() - 1;
                return remainingFileCount > 0
                    ? $"{firstPath} (+{remainingFileCount} more)"
                    : firstPath;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetToolHeaderPathArgument(JsonElement root, string propertyName, out string? propertyValue)
    {
        propertyValue = null;

        if (!root.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        propertyValue = value.Trim();
        return true;
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

    private static void UpdateToolCallArgumentsContent(TextBlock argumentsBlock, TextBlock resultBlock, string toolName, string argumentsJson)
    {
        UpdateToolCallBodyContent(argumentsBlock, resultBlock, FormatToolCallBody(toolName, argumentsJson));
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
        TextBlock? streamContentBlock;
        Border pinnedHeader = CreateSectionHeaderBar(
            section.HeaderText.Text,
            section.Foreground,
            section.HeaderBackground,
            section.BorderBrush,
            out glyphBlock,
            out titleBlock,
            out streamContentBlock);

        pinnedHeader.Margin = new Thickness(0);
        pinnedHeader.HorizontalAlignment = HorizontalAlignment.Stretch;
        pinnedHeader.BorderThickness = new Thickness(0, 1, 0, 1);
        glyphBlock.Text = section.HeaderGlyph.Text;

        // Copy streaming content from the original section (if present)
        if (streamContentBlock is not null && section.HeaderStreamContent is not null)
        {
            streamContentBlock.Text = section.HeaderStreamContent.Text;
        }

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
        if (EnsureActiveConversation().Messages.Count == 0)
        {
            AppendSystemMessage("No conversation to save.");
            return;
        }

        try
        {
            DateTime now = DateTime.Now;
            string timestamp = now.ToString("yyyy-MM-dd_HH-mm-ss");
            string fileName = $"ai-conversation_{timestamp}.txt";
            string tempPath = Path.Combine(Path.GetTempPath(), fileName);
            AiConversation conversation = EnsureActiveConversation();

            System.Text.StringBuilder sb = new();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"AI Chat Conversation Export — {now:g}");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            if (_provider is not null)
            {
                sb.AppendLine($"Provider: {_provider.DisplayName}");
                sb.AppendLine();
            }

            int messageCount = 0;
            foreach (AiChatMessage message in conversation.Messages)
            {
                sb.AppendLine($"[{message.Role}]");
                sb.AppendLine("───────────────────────────────────────────────────────────────");
                sb.AppendLine(message.Content);
                sb.AppendLine();
                messageCount++;
            }

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"Total messages: {messageCount}");
            sb.AppendLine($"Exported: {now:g}");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");

            File.WriteAllText(tempPath, sb.ToString(), System.Text.Encoding.UTF8);

            using Process process = new()
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

    /// <summary>
    /// Display model for the agent session selector dropdown.
    /// Wraps an <see cref="Services.Ai.Agents.IAgent"/> with UI-friendly
    /// display properties.
    /// </summary>
    private sealed class AgentSessionItem
    {
        public Services.Ai.Agents.IAgent Agent { get; }

        /// <summary>
        /// Glyph indicating the agent's role: 👑 for Root, 🔧 for SubAgent.
        /// </summary>
        public string RoleGlyph => Agent.Role switch
        {
            Services.Ai.Agents.AgentRole.Root => "\U0001F451",    // 👑 crown
            Services.Ai.Agents.AgentRole.SubAgent => "\U0001F527", // 🔧 wrench
            _ => "\U0001F916"                                      // 🤖 robot fallback
        };

        /// <summary>
        /// First line in the dropdown: display name with role badge.
        /// </summary>
        public string DisplayLabel
        {
            get
            {
                string roleLabel = Agent.Role switch
                {
                    Services.Ai.Agents.AgentRole.Root => "",
                    Services.Ai.Agents.AgentRole.SubAgent => "[sub] ",
                    _ => ""
                };

                return $"{roleLabel}{Agent.DisplayName}";
            }
        }

        /// <summary>
        /// Second line in the dropdown: model and provider info.
        /// </summary>
        public string Subtitle
        {
            get
            {
                string modelInfo = $"{Agent.Provider.DisplayName} · {Agent.Model}";
                int messageCount = Agent.Messages.Count;

                if (Agent.ChildIds.Count > 0)
                {
                    return $"{modelInfo} · {messageCount} msgs · {Agent.ChildIds.Count} children";
                }

                return $"{modelInfo} · {messageCount} msgs";
            }
        }

        public AgentSessionItem(Services.Ai.Agents.IAgent agent)
        {
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        }
    }
}
