using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using KaneCode.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// An AvalonEdit margin that draws VS Code-style git change indicators (a thin colored
/// bar on the far-left edge of the editor) for lines that differ from HEAD.
/// </summary>
internal sealed class GitChangeMargin : AbstractMargin
{
    private const double MarginWidth = 5.0;

    private static readonly Brush s_addedBrush = new SolidColorBrush(Color.FromRgb(115, 201, 145));
    private static readonly Brush s_modifiedBrush = new SolidColorBrush(Color.FromRgb(86, 156, 214));
    private static readonly Brush s_deletedBrush = new SolidColorBrush(Color.FromRgb(244, 71, 71));

    private IReadOnlyList<GitLineChange> _changes = [];

    static GitChangeMargin()
    {
        if (s_addedBrush.CanFreeze) s_addedBrush.Freeze();
        if (s_modifiedBrush.CanFreeze) s_modifiedBrush.Freeze();
        if (s_deletedBrush.CanFreeze) s_deletedBrush.Freeze();
    }

    /// <summary>
    /// Replaces the change markers shown by this margin and repaints it.
    /// </summary>
    public void UpdateChanges(IReadOnlyList<GitLineChange> changes)
    {
        _changes = changes ?? [];
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(MarginWidth, 0);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_changes.Count == 0)
        {
            return;
        }

        TextView? textView = TextView;
        if (textView is null || textView.Document is null)
        {
            return;
        }

        foreach (GitLineChange change in _changes)
        {
            if (change.LineNumber < 1 || change.LineNumber > textView.Document.LineCount)
            {
                continue;
            }

            VisualLine? visualLine = FindVisualLineForDocumentLine(change.LineNumber);
            if (visualLine is null || visualLine.TextLines.Count == 0)
            {
                continue;
            }

            double y = visualLine.GetTextLineVisualYPosition(
                visualLine.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
            double height = visualLine.TextLines[0].Height;

            drawingContext.DrawRectangle(GetBrush(change.ChangeType), null, new Rect(0, y, MarginWidth, height));
        }
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private VisualLine? FindVisualLineForDocumentLine(int lineNumber)
    {
        TextView? textView = TextView;
        if (textView is null || !textView.VisualLinesValid)
        {
            return null;
        }

        foreach (VisualLine visualLine in textView.VisualLines)
        {
            if (visualLine.FirstDocumentLine.LineNumber == lineNumber)
            {
                return visualLine;
            }
        }

        return null;
    }

    private static Brush GetBrush(GitLineChangeType changeType) => changeType switch
    {
        GitLineChangeType.Added => s_addedBrush,
        GitLineChangeType.Deleted => s_deletedBrush,
        _ => s_modifiedBrush
    };
}
