using KaneCode.Models;
using KaneCode.Services.Ai;
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
    private IAiProvider? _provider;
    private string? _model;
    private CancellationTokenSource? _streamCts;
    private bool _isStreaming;
    private AiUsageStats? _lastUsageStats;
    private Func<IReadOnlyList<ProjectItem>>? _projectItemsProvider;
    private Func<string?>? _projectConversationKeyProvider;
    private ListBox? _mentionPopup;
    private string? _pendingSelectionContext;
    private bool _projectContextInjected;
    private const int OutboundTokenBudget = 12000;

    public AiChatPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Configures the provider and model to use for chat completions.
    /// </summary>
    internal void Configure(IAiProvider? provider, string? model = null)
    {
        _provider = provider;
        _model = model;
        _projectContextInjected = false;
        ProviderLabel.Text = provider is not null
            ? $"{provider.DisplayName}"
            : "No provider";
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

    private IReadOnlyList<AiChatMessage> BuildBudgetedContextWindow()
    {
        if (_conversationHistory.Count == 0)
        {
            return [];
        }

        var total = 0;
        var selected = new List<AiChatMessage>();

        for (var i = _conversationHistory.Count - 1; i >= 0; i--)
        {
            var message = _conversationHistory[i];
            var cost = EstimateTokens(message.Content);

            if (selected.Count > 0 && total + cost > OutboundTokenBudget)
            {
                break;
            }

            selected.Add(message);
            total += cost;
        }

        selected.Reverse();
        return selected;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 1;
        }

        // Heuristic token estimate (roughly 4 chars/token average)
        return Math.Max(1, text.Length / 4);
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
        MessagePanel.Children.Clear();
        _pendingSelectionContext = null;
        _projectContextInjected = false;

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
    }

    private async Task SendMessageAsync()
    {
        var text = InputBox.Text?.Trim();
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

        // Display user message
        AppendUserMessage(text);

        // Inject project-wide system context once per conversation
        if (!_projectContextInjected)
        {
            var projectItems = _projectItemsProvider?.Invoke() ?? [];
            var projectContext = AiProjectContextBuilder.Build(projectItems);

            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                _conversationHistory.Add(new AiChatMessage(AiChatRole.System, projectContext));
            }

            _projectContextInjected = true;
        }

        // Add to history with context injection (references + one-shot selection context)
        var referenceContext = BuildReferenceContext();

        var combinedContext = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(referenceContext))
        {
            combinedContext.AppendLine(referenceContext);
        }

        if (!string.IsNullOrEmpty(_pendingSelectionContext))
        {
            combinedContext.AppendLine(_pendingSelectionContext);
            _pendingSelectionContext = null; // one-shot context
        }

        if (combinedContext.Length > 0)
        {
            var userMessageWithContext = $"{combinedContext}\n{text}";
            _conversationHistory.Add(new AiChatMessage(AiChatRole.User, userMessageWithContext));
        }
        else
        {
            _conversationHistory.Add(new AiChatMessage(AiChatRole.User, text));
        }

        SavePersistedConversation();

        // Stream assistant response
        _isStreaming = true;
        SendButton.IsEnabled = true;
        SendButton.Content = "⏹ Stop";
        SendButton.Click -= SendButton_Click;
        SendButton.Click += StopButton_Click;

        var responseBuilder = new System.Text.StringBuilder();
        var reasoningBuilder = new System.Text.StringBuilder();
        var assistantBlock = CreateAssistantMessageBlock();
        Expander? thinkingExpander = null;
        TextBlock? thinkingTextBlock = null;
        var reasoningTokenCount = 0;
        var contentTokenCount = 0;
        var streamStopwatch = Stopwatch.StartNew();

        CancelStreaming();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        try
        {
            var model = _model ?? _provider.AvailableModels.FirstOrDefault() ?? "default";

            var outboundWindow = BuildBudgetedContextWindow();

            await foreach (var token in _provider.StreamCompletionAsync(outboundWindow, model, ct)
                .ConfigureAwait(false))
            {
                if (token.Type == AiStreamTokenType.Usage)
                {
                    _lastUsageStats = token.UsageStats;
                    continue;
                }

                if (token.Type == AiStreamTokenType.Reasoning)
                {
                    reasoningBuilder.Append(token.Text);
                    reasoningTokenCount++;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var shouldStickToBottom = IsMessageScrollerNearBottom();

                        if (thinkingExpander is null)
                        {
                            (thinkingExpander, thinkingTextBlock) = CreateThinkingExpander(assistantBlock);
                        }

                        thinkingTextBlock!.Text = reasoningBuilder.ToString();
                        thinkingExpander.Header = $"💭 Thinking ({reasoningTokenCount:N0} tokens)...";

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

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var shouldStickToBottom = IsMessageScrollerNearBottom();

                        // Finalize the thinking header once content starts
                        if (thinkingExpander is not null &&
                            thinkingExpander.Header is string header &&
                            header.EndsWith("...", StringComparison.Ordinal))
                        {
                            thinkingExpander.Header = $"💭 Thought for {reasoningTokenCount:N0} tokens";
                        }

                        RenderMarkdownInto(assistantBlock, responseBuilder.ToString());
                        UpdateStatsBar(reasoningTokenCount + contentTokenCount, streamStopwatch);

                        if (shouldStickToBottom)
                        {
                            MessageScroller.ScrollToEnd();
                        }
                    });
                }
            }

            streamStopwatch.Stop();

            // Record final response in history
            _conversationHistory.Add(new AiChatMessage(AiChatRole.Assistant, responseBuilder.ToString()));
            SavePersistedConversation();
        }
        catch (OperationCanceledException)
        {
            // User cancelled — keep what was streamed so far
            if (responseBuilder.Length > 0)
            {
                _conversationHistory.Add(new AiChatMessage(AiChatRole.Assistant, responseBuilder.ToString()));
                SavePersistedConversation();
            }
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
            });
        }
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
        var elapsed = stopwatch.Elapsed.TotalSeconds;
        var tokPerSec = elapsed > 0.1 ? totalTokens / elapsed : 0;
        StatsBar.Text = $"{totalTokens:N0} tokens  •  {tokPerSec:F1} tok/s";
    }

    /// <summary>
    /// Updates the stats bar with final summary after streaming completes.
    /// Shows prompt tokens (context), completion breakdown, and tokens/sec.
    /// </summary>
    private void UpdateStatsBarFinal(int totalGenerated, int reasoningTokens, int contentTokens, Stopwatch stopwatch)
    {
        var elapsed = stopwatch.Elapsed.TotalSeconds;
        var tokPerSec = elapsed > 0.1 ? totalGenerated / elapsed : 0;

        var parts = new List<string>();

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

    // ── Message rendering ──────────────────────────────────────────

    private void AppendUserMessage(string text)
    {
        var border = new Border
        {
            Background = FindBrush("AiChatUserBubble"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(40, 4, 4, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var rtb = new RichTextBox
        {
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = FindBrush("AiChatUserForeground"),
            FontSize = 13,
            Padding = new Thickness(0),
            IsDocumentEnabled = true
        };

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            LineHeight = 18
        };

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            Foreground = FindBrush("AiChatUserForeground")
        };
        paragraph.Inlines.Add(new Run(text));
        doc.Blocks.Add(paragraph);

        rtb.Document = doc;
        border.Child = rtb;
        MessagePanel.Children.Add(border);
        MessageScroller.ScrollToEnd();
    }

    /// <summary>
    /// Creates an empty assistant message block and returns the RichTextBox for progressive rendering.
    /// Assistant content is rendered full-width (no bubble) for readability in narrow panels.
    /// </summary>
    private RichTextBox CreateAssistantMessageBlock()
    {
        var rtb = new RichTextBox
        {
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = FindBrush("AiChatAssistantForeground"),
            FontSize = 14,
            FontFamily = new FontFamily("Segoe UI"),
            IsDocumentEnabled = true,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 4, 4, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Single-column flow and tighter padding for readable assistant output in narrow panes
        rtb.Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            LineHeight = 20
        };

        MessagePanel.Children.Add(rtb);
        return rtb;
    }

    /// <summary>
    /// Creates a collapsible thinking/reasoning expander (collapsed by default)
    /// and adds it to the message panel. If <paramref name="insertBefore"/> is supplied,
    /// the expander is inserted before that element.
    /// Returns both the expander and the inner TextBlock so reasoning tokens
    /// can be appended progressively.
    /// </summary>
    private (Expander expander, TextBlock textBlock) CreateThinkingExpander(UIElement? insertBefore = null)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = FindBrush("AiChatThinkingForeground"),
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 4, 0, 0)
        };

        var expander = new Expander
        {
            Header = "💭 Thinking...",
            IsExpanded = false,
            Foreground = FindBrush("AiChatThinkingForeground"),
            FontSize = 12,
            Margin = new Thickness(4, 4, 40, 0),
            Content = new Border
            {
                Background = FindBrush("AiChatThinkingBackground"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                BorderBrush = FindBrush("AiChatThinkingBorder"),
                BorderThickness = new Thickness(1),
                Child = textBlock
            }
        };

        if (insertBefore is not null)
        {
            var index = MessagePanel.Children.IndexOf(insertBefore);
            if (index >= 0)
            {
                MessagePanel.Children.Insert(index, expander);
            }
            else
            {
                MessagePanel.Children.Add(expander);
            }
        }
        else
        {
            MessagePanel.Children.Add(expander);
        }

        return (expander, textBlock);
    }

    private void AppendSystemMessage(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            Foreground = FindBrush("AiChatSecondaryForeground"),
            FontSize = 12,
            Margin = new Thickness(4, 4, 4, 4)
        };

        MessagePanel.Children.Add(tb);
        MessageScroller.ScrollToEnd();
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

            if (!string.IsNullOrWhiteSpace(ProviderLabel.Text))
            {
                sb.AppendLine($"Provider: {ProviderLabel.Text}");
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
