using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using KaneCode.Theming;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// An AvalonEdit margin that displays a lightbulb glyph on lines where
/// Roslyn code actions (fixes or refactorings) are available.
/// Clicking the glyph triggers the code action popup.
/// </summary>
internal sealed class LightBulbMargin : AbstractMargin
{
    private const double MarginWidth = 20.0;
    private const double GlyphFontSize = 14.0;
    private const string LightBulbGlyph = "\U0001F4A1"; // 💡

    private int _actionLineNumber;
    private bool _hasActions;

    /// <summary>
    /// Raised when the user clicks the lightbulb glyph to request showing code actions.
    /// </summary>
    public event EventHandler? GlyphClicked;

    /// <summary>
    /// Gets the 1-based line number where the lightbulb is currently shown,
    /// or 0 if no lightbulb is visible.
    /// </summary>
    public int ActionLineNumber => _hasActions ? _actionLineNumber : 0;

    /// <summary>
    /// Shows the lightbulb glyph on the specified editor line.
    /// </summary>
    /// <param name="lineNumber">1-based line number.</param>
    public void ShowOnLine(int lineNumber)
    {
        if (_hasActions && _actionLineNumber == lineNumber)
        {
            return;
        }

        _actionLineNumber = lineNumber;
        _hasActions = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Hides the lightbulb glyph.
    /// </summary>
    public void Hide()
    {
        if (!_hasActions)
        {
            return;
        }

        _hasActions = false;
        _actionLineNumber = 0;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(MarginWidth, 0);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!_hasActions || _actionLineNumber < 1)
        {
            return;
        }

        TextView? textView = TextView;
        if (textView is null || textView.Document is null)
        {
            return;
        }

        if (_actionLineNumber > textView.Document.LineCount)
        {
            return;
        }

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        VisualLine? visualLine = FindVisualLineForDocumentLine(_actionLineNumber);
        if (visualLine is null)
        {
            return;
        }

        double y = visualLine.GetTextLineVisualYPosition(
            visualLine.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;

        double lineHeight = visualLine.TextLines[0].Height;

        Brush foreground = ResolveGlyphBrush();

        FormattedText formattedText = new(
            LightBulbGlyph,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Emoji"),
            GlyphFontSize,
            foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double glyphX = (MarginWidth - formattedText.Width) / 2.0;
        double glyphY = y + (lineHeight - formattedText.Height) / 2.0;

        drawingContext.DrawText(formattedText, new Point(glyphX, glyphY));
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!_hasActions || TextView is null)
        {
            return;
        }

        if (IsOverGlyph(e.GetPosition(this)))
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        base.OnMouseLeftButtonUp(e);

        if (!_hasActions || TextView is null)
        {
            return;
        }

        if (IsOverGlyph(e.GetPosition(this)))
        {
            GlyphClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private bool IsOverGlyph(Point position)
    {
        VisualLine? visualLine = FindVisualLineForDocumentLine(_actionLineNumber);
        if (visualLine is null)
        {
            return false;
        }

        double lineTop = visualLine.GetTextLineVisualYPosition(
            visualLine.TextLines[0], VisualYPosition.TextTop) - TextView!.VerticalOffset;
        double lineBottom = lineTop + visualLine.TextLines[0].Height;

        return position.Y >= lineTop && position.Y <= lineBottom;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        // Accept hit tests across the entire margin so the cursor changes
        return new PointHitTestResult(this, hitTestParameters.HitPoint);
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

        foreach (VisualLine vl in textView.VisualLines)
        {
            if (vl.FirstDocumentLine.LineNumber == lineNumber)
            {
                return vl;
            }
        }

        return null;
    }

    private static Brush ResolveGlyphBrush()
    {
        if (Application.Current.TryFindResource(ThemeResourceKeys.DiagnosticWarningForeground) is Brush themeBrush)
        {
            return themeBrush;
        }

        return Brushes.Gold;
    }
}
