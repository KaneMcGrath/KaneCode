using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
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
/// </summary>
internal sealed class RoslynCompletionData : ICompletionData
{
    private readonly Microsoft.CodeAnalysis.Completion.CompletionItem _roslynItem;
    private readonly Document _document;
    private readonly CompletionService _completionService;
    private string? _descriptionText;

    public RoslynCompletionData(
        Microsoft.CodeAnalysis.Completion.CompletionItem roslynItem,
        Document document,
        CompletionService completionService)
    {
        _roslynItem = roslynItem;
        _document = document;
        _completionService = completionService;
    }

    public ImageSource? Image => null;

    public string Text => _roslynItem.DisplayText;

    public object Content => _roslynItem.DisplayTextPrefix + _roslynItem.DisplayText + _roslynItem.DisplayTextSuffix;

    public object Description
    {
        get
        {
            if (_descriptionText is not null)
            {
                return _descriptionText;
            }

            // Fetch description synchronously for the tooltip (lazy)
            try
            {
                var descriptionTask = _completionService.GetDescriptionAsync(_document, _roslynItem);
                if (descriptionTask.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    _descriptionText = descriptionTask.Result?.Text ?? string.Empty;
                }
                else
                {
                    _descriptionText = string.Empty;
                }
            }
            catch
            {
                _descriptionText = string.Empty;
            }

            return _descriptionText;
        }
    }

    public double Priority => _roslynItem.Rules.MatchPriority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        try
        {
            var change = _completionService.GetChangeAsync(_document, _roslynItem).GetAwaiter().GetResult();
            if (change?.TextChange is { } textChange)
            {
                var doc = textArea.Document;
                // Replace the span that Roslyn suggests
                var start = textChange.Span.Start;
                var length = textChange.Span.Length;

                // Clamp to document bounds
                if (start < 0) start = 0;
                if (start + length > doc.TextLength)
                {
                    length = doc.TextLength - start;
                }

                doc.Replace(start, length, textChange.NewText);
            }
            else
            {
                // Fallback: simple text insertion
                textArea.Document.Replace(completionSegment, Text);
            }
        }
        catch
        {
            // Fallback on any error
            textArea.Document.Replace(completionSegment, Text);
        }
    }
}
