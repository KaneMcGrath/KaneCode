using ICSharpCode.AvalonEdit;
using KaneCode.Controls;
using KaneCode.Models;
using KaneCode.Theming;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// Coordinates an inline rename session: shows the adorner with a TextBox
/// at the primary span, highlights secondary spans, and optionally displays
/// a preview panel listing affected files before the rename is committed.
/// </summary>
internal sealed class InlineRenameSession : IDisposable
{
    private readonly TextEditor _editor;
    private readonly RoslynRefactoringService _refactoringService;
    private readonly RoslynWorkspaceService _roslynService;
    private readonly string _filePath;
    private readonly int _caretOffset;
    private InlineRenameAdorner? _adorner;
    private InlineRenameInfo? _renameInfo;
    private IReadOnlyList<RenamePreviewItem>? _previewItems;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Raised when the rename session is committed with a new name.</summary>
    public event EventHandler<InlineRenameCommitArgs>? Committed;

    /// <summary>Raised when the rename session is cancelled.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Raised when the preview data is loaded and ready for display.</summary>
    public event EventHandler<IReadOnlyList<RenamePreviewItem>>? PreviewReady;

    public InlineRenameSession(
        TextEditor editor,
        RoslynRefactoringService refactoringService,
        RoslynWorkspaceService roslynService,
        string filePath,
        int caretOffset)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(refactoringService);
        ArgumentNullException.ThrowIfNull(roslynService);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _editor = editor;
        _refactoringService = refactoringService;
        _roslynService = roslynService;
        _filePath = filePath;
        _caretOffset = caretOffset;
    }

    /// <summary>True when the adorner is currently displayed and the session is active.</summary>
    public bool IsActive => _adorner is not null;

    /// <summary>
    /// Starts the inline rename session by finding the symbol, computing spans,
    /// and displaying the adorner.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken ct = _cts.Token;

        // Sync the document text to Roslyn before analysis
        await _roslynService.UpdateDocumentTextAsync(_filePath, _editor.Text, ct).ConfigureAwait(true);

        _renameInfo = await _refactoringService
            .FindRenameSpansAsync(_filePath, _caretOffset, ct)
            .ConfigureAwait(true);

        if (_renameInfo is null || _renameInfo.Spans.Count == 0)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Find the span closest to the caret to use as the primary (editable) span
        int primaryIndex = FindPrimarySpanIndex(_renameInfo.Spans, _caretOffset);

        AdornerLayer? adornerLayer = AdornerLayer.GetAdornerLayer(_editor.TextArea.TextView);
        if (adornerLayer is null)
        {
            Debug.WriteLine("[InlineRenameSession] No adorner layer available.");
            Cancelled?.Invoke(this, EventArgs.Empty);
            return;
        }

        _adorner = new InlineRenameAdorner(_editor, _renameInfo.Spans, primaryIndex, _renameInfo.SymbolName);
        _adorner.Committed += OnAdornerCommitted;
        _adorner.Cancelled += OnAdornerCancelled;

        adornerLayer.Add(_adorner);
        _adorner.FocusRenameBox();

        // Load preview asynchronously (don't block the session start)
        _ = LoadPreviewAsync(ct);
    }

    /// <summary>
    /// Cancels the rename session if it is active.
    /// </summary>
    public void Cancel()
    {
        if (_adorner is not null)
        {
            DetachAdorner();
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        DetachAdorner();
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            _previewItems = await _refactoringService
                .GetRenamePreviewAsync(_filePath, _caretOffset, cancellationToken)
                .ConfigureAwait(true);

            if (_previewItems.Count > 0)
            {
                PreviewReady?.Invoke(this, _previewItems);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended before preview loaded
        }
    }

    private void OnAdornerCommitted(object? sender, string newName)
    {
        string originalName = _renameInfo?.SymbolName ?? string.Empty;
        IReadOnlyList<RenamePreviewItem>? preview = _previewItems;
        DetachAdorner();

        Committed?.Invoke(this, new InlineRenameCommitArgs(
            _filePath,
            _caretOffset,
            originalName,
            newName,
            preview ?? []));
    }

    private void OnAdornerCancelled(object? sender, EventArgs e)
    {
        DetachAdorner();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void DetachAdorner()
    {
        if (_adorner is not null)
        {
            _adorner.Committed -= OnAdornerCommitted;
            _adorner.Cancelled -= OnAdornerCancelled;
            _adorner.Detach();
            _adorner = null;
        }
    }

    private static int FindPrimarySpanIndex(IReadOnlyList<InlineRenameSpan> spans, int caretOffset)
    {
        int bestIndex = 0;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < spans.Count; i++)
        {
            int spanEnd = spans[i].Start + spans[i].Length;
            int distance;

            if (caretOffset >= spans[i].Start && caretOffset <= spanEnd)
            {
                // Caret is inside this span — perfect match
                return i;
            }

            distance = Math.Min(
                Math.Abs(caretOffset - spans[i].Start),
                Math.Abs(caretOffset - spanEnd));

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}

/// <summary>
/// Arguments for the <see cref="InlineRenameSession.Committed"/> event,
/// carrying all information needed to execute the Roslyn rename.
/// </summary>
internal sealed record InlineRenameCommitArgs(
    string FilePath,
    int CaretOffset,
    string OriginalName,
    string NewName,
    IReadOnlyList<RenamePreviewItem> AffectedFiles);
