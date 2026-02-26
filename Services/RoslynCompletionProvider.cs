using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using KaneCode.Theming;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// Provides Roslyn-powered code completion for the AvalonEdit editor.
/// </summary>
internal sealed class RoslynCompletionProvider
{
    private readonly RoslynWorkspaceService _roslynService;

    public RoslynCompletionProvider(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;
    }

    /// <summary>
    /// Gets completion items at the specified caret offset after ensuring the workspace text is up-to-date.
    /// </summary>
    public async Task<CompletionResult?> GetCompletionsAsync(
        string filePath,
        string currentText,
        int caretOffset,
        CancellationToken cancellationToken = default)
    {
        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return null;
        }

        // Push the latest editor text into Roslyn so completions reflect the current state
        await _roslynService.UpdateDocumentTextAsync(filePath, currentText, cancellationToken).ConfigureAwait(false);

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return null;
        }

        var completionService = CompletionService.GetService(document);
        if (completionService is null)
        {
            return null;
        }

        var completionList = await completionService.GetCompletionsAsync(
            document, caretOffset, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (completionList is null || completionList.ItemsList.Count == 0)
        {
            return null;
        }

        var results = new List<RoslynCompletionData>(completionList.ItemsList.Count);
        foreach (var item in completionList.ItemsList)
        {
            results.Add(new RoslynCompletionData(item, document, completionService));
        }

        // Roslyn tells us the span it considers the "filter text" for the completion.
        // Use its start as the CompletionWindow.StartOffset so AvalonEdit filters as you type.
        var defaultSpanStart = completionList.Span.Start;

        return new CompletionResult(results, defaultSpanStart);
    }

    /// <summary>
    /// Determines whether completion should be auto-triggered for the given character.
    /// </summary>
    public static bool ShouldAutoTrigger(char typedChar)
    {
        return typedChar == '.' || char.IsLetter(typedChar) || typedChar == '_';
    }

    /// <summary>
    /// Applies theme colors to a <see cref="CompletionWindow"/> so text is readable in dark themes.
    /// </summary>
    public static void ApplyTheme(CompletionWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var bgBrush = Application.Current.TryFindResource(ThemeResourceKeys.CompletionBackground) as Brush;
        var fgBrush = Application.Current.TryFindResource(ThemeResourceKeys.CompletionForeground) as Brush;

        if (bgBrush is not null)
        {
            window.Background = bgBrush;
        }

        if (fgBrush is not null)
        {
            window.Foreground = fgBrush;
        }

        if (Application.Current.TryFindResource(ThemeResourceKeys.CompletionBorder) is Brush borderBrush)
        {
            window.BorderBrush = borderBrush;
            window.BorderThickness = new Thickness(1);
        }

        // Style the inner ListBox so items inherit the foreground color
        var listBox = FindCompletionListBox(window);
        if (listBox is not null)
        {
            listBox.Foreground = fgBrush ?? SystemColors.ControlTextBrush;
            listBox.Background = bgBrush ?? SystemColors.WindowBrush;
        }
    }

    /// <summary>
    /// Walks the visual/logical tree of the CompletionWindow to find the inner ListBox.
    /// </summary>
    private static ListBox? FindCompletionListBox(CompletionWindow window)
    {
        // CompletionWindow.CompletionList is a CompletionList control that contains a ListBox
        var completionList = window.CompletionList;
        if (completionList is null)
        {
            return null;
        }

        return completionList.ListBox;
    }
}

/// <summary>
/// Result of a completion query, containing items and the span start for filtering.
/// </summary>
internal sealed record CompletionResult(IReadOnlyList<RoslynCompletionData> Items, int SpanStart);

/// <summary>
/// An AvalonEdit completion data item backed by a Roslyn <see cref="CompletionItem"/>.
/// Description is pre-fetched asynchronously; completion change is fetched on demand at insertion time.
/// </summary>
internal sealed class RoslynCompletionData : ICompletionData
{
    private readonly Microsoft.CodeAnalysis.Completion.CompletionItem _roslynItem;
    private readonly Document _document;
    private readonly CompletionService _completionService;

    private readonly Task<string> _descriptionTask;

    public RoslynCompletionData(
        Microsoft.CodeAnalysis.Completion.CompletionItem roslynItem,
        Document document,
        CompletionService completionService)
    {
        _roslynItem = roslynItem;
        _document = document;
        _completionService = completionService;

        _descriptionTask = FetchDescriptionAsync();
    }

    public ImageSource? Image => null;

    public string Text => _roslynItem.DisplayText;

    public object Content => _roslynItem.DisplayTextPrefix + _roslynItem.DisplayText + _roslynItem.DisplayTextSuffix;

    public object Description
    {
        get
        {
            if (_descriptionTask.IsCompletedSuccessfully)
            {
                return _descriptionTask.Result;
            }

            return _roslynItem.DisplayText;
        }
    }

    public double Priority => _roslynItem.Rules.MatchPriority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        try
        {
            // Fetch the completion change to get the intended replacement text (may include
            // extras like parentheses or generic parameters beyond the display text).
            string insertText = Text;
            try
            {
                var change = _completionService.GetChangeAsync(_document, _roslynItem).GetAwaiter().GetResult();
                if (change?.TextChange is { } textChange)
                {
                    insertText = textChange.NewText;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to fetch completion change: {ex.Message}");
            }

            // Always use AvalonEdit's completionSegment for the replacement range.
            // The Roslyn TextChange.Span was computed against a stale document snapshot
            // captured when the completion window opened, so it doesn't account for
            // characters typed while the window was filtering.
            textArea.Document.Replace(completionSegment, insertText);
        }
        catch
        {
            textArea.Document.Replace(completionSegment, Text);
        }
    }

    private async Task<string> FetchDescriptionAsync()
    {
        try
        {
            var description = await _completionService.GetDescriptionAsync(_document, _roslynItem).ConfigureAwait(false);
            return description?.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to fetch completion description: {ex.Message}");
            return string.Empty;
        }
    }
}
