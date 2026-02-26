using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using System.Diagnostics;
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
    /// Gets completion items at the specified caret offset.
    /// </summary>
    public async Task<IReadOnlyList<RoslynCompletionData>> GetCompletionsAsync(
        string filePath,
        int caretOffset,
        CancellationToken cancellationToken = default)
    {
        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return [];
        }

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return [];
        }

        var completionService = CompletionService.GetService(document);
        if (completionService is null)
        {
            return [];
        }

        var completionList = await completionService.GetCompletionsAsync(
            document, caretOffset, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (completionList is null)
        {
            return [];
        }

        var results = new List<RoslynCompletionData>(completionList.ItemsList.Count);
        foreach (var item in completionList.ItemsList)
        {
            results.Add(new RoslynCompletionData(item, document, completionService));
        }

        return results;
    }

    /// <summary>
    /// Determines whether completion should be triggered for the given character.
    /// </summary>
    public static bool ShouldTriggerCompletion(char typedChar)
    {
        return typedChar == '.' || char.IsLetter(typedChar);
    }
}

/// <summary>
/// An AvalonEdit completion data item backed by a Roslyn <see cref="CompletionItem"/>.
/// Descriptions and completion changes are pre-fetched asynchronously to avoid blocking the UI thread.
/// </summary>
internal sealed class RoslynCompletionData : ICompletionData
{
    private readonly Microsoft.CodeAnalysis.Completion.CompletionItem _roslynItem;
    private readonly Document _document;
    private readonly CompletionService _completionService;

    private readonly Task<string> _descriptionTask;
    private readonly Task<CompletionChange?> _changeTask;

    public RoslynCompletionData(
        Microsoft.CodeAnalysis.Completion.CompletionItem roslynItem,
        Document document,
        CompletionService completionService)
    {
        _roslynItem = roslynItem;
        _document = document;
        _completionService = completionService;

        // Pre-fetch description and completion change on a background thread
        _descriptionTask = FetchDescriptionAsync();
        _changeTask = FetchChangeAsync();
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
            var change = _changeTask.IsCompletedSuccessfully ? _changeTask.Result : null;

            if (change?.TextChange is { } textChange)
            {
                var doc = textArea.Document;
                var start = textChange.Span.Start;
                var length = textChange.Span.Length;

                // Clamp to document bounds
                if (start < 0)
                {
                    start = 0;
                }

                if (start + length > doc.TextLength)
                {
                    length = doc.TextLength - start;
                }

                doc.Replace(start, length, textChange.NewText);
            }
            else
            {
                // Fallback: simple text insertion (pre-fetch not ready or returned null)
                textArea.Document.Replace(completionSegment, Text);
            }
        }
        catch
        {
            // Fallback on any error
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

    private async Task<CompletionChange?> FetchChangeAsync()
    {
        try
        {
            return await _completionService.GetChangeAsync(_document, _roslynItem).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to fetch completion change: {ex.Message}");
            return null;
        }
    }
}
