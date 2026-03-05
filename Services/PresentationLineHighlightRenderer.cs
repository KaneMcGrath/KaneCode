using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System.Windows;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// Draws a subtle background highlight behind a specific editor line.
/// </summary>
internal sealed class PresentationLineHighlightRenderer : IBackgroundRenderer
{
    private static readonly Brush s_highlightBrush = CreateHighlightBrush();

    private int _lineNumber;

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>
    /// Sets the 1-based line number to highlight. Set to 0 to clear.
    /// </summary>
    public void SetHighlightedLine(int lineNumber)
    {
        _lineNumber = lineNumber;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_lineNumber < 1)
        {
            return;
        }

        TextDocument? document = textView.Document;
        if (document is null || _lineNumber > document.LineCount)
        {
            return;
        }

        DocumentLine line = document.GetLineByNumber(_lineNumber);
        TextSegment segment = new() { StartOffset = line.Offset, EndOffset = Math.Max(line.EndOffset, line.Offset + 1) };

        foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
        {
            drawingContext.DrawRectangle(s_highlightBrush, null, rect);
        }
    }

    private static Brush CreateHighlightBrush()
    {
        SolidColorBrush brush = new(Color.FromArgb(52, 255, 235, 140));
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }
}
