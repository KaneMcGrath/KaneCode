using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using KaneCode.Models;
using KaneCode.Theming;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// An adorner overlaid on the AvalonEdit <see cref="TextEditor"/> that implements
/// inline rename: a TextBox replaces the primary symbol span, while all other
/// occurrences are highlighted with a background rectangle and updated live.
/// </summary>
internal sealed class InlineRenameAdorner : Adorner
{
    private readonly TextEditor _editor;
    private readonly TextBox _renameBox;
    private readonly IReadOnlyList<InlineRenameSpan> _spans;
    private readonly int _primaryIndex;
    private readonly string _originalName;
    private bool _committed;

    /// <summary>Raised when the user presses Enter to commit the rename.</summary>
    public event EventHandler<string>? Committed;

    /// <summary>Raised when the user presses Escape or clicks away to cancel the rename.</summary>
    public event EventHandler? Cancelled;

    public InlineRenameAdorner(TextEditor editor, IReadOnlyList<InlineRenameSpan> spans, int primaryIndex, string originalName)
        : base(editor.TextArea.TextView)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(originalName);

        _editor = editor;
        _spans = spans;
        _primaryIndex = primaryIndex;
        _originalName = originalName;

        _renameBox = new TextBox
        {
            Text = originalName,
            FontFamily = editor.FontFamily,
            FontSize = editor.FontSize,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(1),
            MinWidth = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        ApplyThemeBrushes();
        _renameBox.SelectAll();

        _renameBox.TextChanged += OnRenameTextChanged;
        _renameBox.PreviewKeyDown += OnRenameKeyDown;
        _renameBox.LostFocus += OnRenameLostFocus;

        AddVisualChild(_renameBox);
        AddLogicalChild(_renameBox);

        // Scroll handler to invalidate positioning when the editor scrolls
        _editor.TextArea.TextView.ScrollOffsetChanged += OnScrollChanged;

        IsHitTestVisible = true;
    }

    /// <summary>
    /// The current text in the rename box.
    /// </summary>
    public string CurrentName => _renameBox.Text;

    /// <summary>
    /// Focuses the rename TextBox so the user can start typing immediately.
    /// </summary>
    public void FocusRenameBox()
    {
        _renameBox.Focus();
        _renameBox.SelectAll();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index)
    {
        return index == 0 ? _renameBox : throw new ArgumentOutOfRangeException(nameof(index));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _renameBox.Measure(constraint);
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Rect primaryRect = GetPrimarySpanRect();
        if (primaryRect != Rect.Empty)
        {
            // Size the textbox to fit the text content, but at least as wide as the original span
            double width = Math.Max(_renameBox.DesiredSize.Width, primaryRect.Width);
            _renameBox.Arrange(new Rect(primaryRect.TopLeft, new Size(width, primaryRect.Height)));
        }
        else
        {
            _renameBox.Arrange(new Rect(0, 0, 0, 0));
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        Brush? highlightBg = Application.Current.TryFindResource(ThemeResourceKeys.RenameHighlightBackground) as Brush;
        Brush? highlightBorder = Application.Current.TryFindResource(ThemeResourceKeys.RenameHighlightBorder) as Brush;
        Pen? borderPen = highlightBorder is not null ? new Pen(highlightBorder, 1.0) : null;
        borderPen?.Freeze();

        TextDocument document = _editor.Document;
        TextView textView = _editor.TextArea.TextView;
        string newName = _renameBox.Text;
        int lengthDelta = newName.Length - _originalName.Length;

        for (int i = 0; i < _spans.Count; i++)
        {
            if (i == _primaryIndex)
            {
                continue;
            }

            // Adjust offset for spans that come after the primary span, because
            // the document text has been modified by the live rename edits.
            InlineRenameSpan span = _spans[i];
            int adjustedStart = span.Start;
            if (span.Start > _spans[_primaryIndex].Start)
            {
                adjustedStart += lengthDelta;
            }

            if (adjustedStart < 0 || adjustedStart + newName.Length > document.TextLength)
            {
                continue;
            }

            TextSegment segment = new() { StartOffset = adjustedStart, EndOffset = adjustedStart + newName.Length };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                if (highlightBg is not null)
                {
                    drawingContext.DrawRectangle(highlightBg, borderPen, rect);
                }
            }
        }
    }

    /// <summary>
    /// Removes event handlers and detaches the adorner from the adorner layer.
    /// </summary>
    public void Detach()
    {
        _renameBox.TextChanged -= OnRenameTextChanged;
        _renameBox.PreviewKeyDown -= OnRenameKeyDown;
        _renameBox.LostFocus -= OnRenameLostFocus;
        _editor.TextArea.TextView.ScrollOffsetChanged -= OnScrollChanged;

        RemoveVisualChild(_renameBox);
        RemoveLogicalChild(_renameBox);

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(_editor.TextArea.TextView);
        layer?.Remove(this);
    }

    private void OnRenameTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Trigger re-render of highlight rectangles and re-measure for textbox size
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        // When focus leaves the rename box, cancel unless already committed
        if (!_committed)
        {
            Cancel();
        }
    }

    private void OnScrollChanged(object? sender, EventArgs e)
    {
        InvalidateArrange();
        InvalidateVisual();
    }

    private void Commit()
    {
        if (_committed)
        {
            return;
        }

        _committed = true;
        string newName = _renameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(newName) || newName == _originalName)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Committed?.Invoke(this, newName);
        }
    }

    private void Cancel()
    {
        if (_committed)
        {
            return;
        }

        _committed = true;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private Rect GetPrimarySpanRect()
    {
        if (_primaryIndex < 0 || _primaryIndex >= _spans.Count)
        {
            return Rect.Empty;
        }

        InlineRenameSpan primary = _spans[_primaryIndex];
        TextDocument document = _editor.Document;
        TextView textView = _editor.TextArea.TextView;

        if (primary.Start < 0 || primary.Start + primary.Length > document.TextLength)
        {
            return Rect.Empty;
        }

        TextSegment segment = new() { StartOffset = primary.Start, EndOffset = primary.Start + primary.Length };
        foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
        {
            return rect;
        }

        return Rect.Empty;
    }

    private void ApplyThemeBrushes()
    {
        Brush? activeBg = Application.Current.TryFindResource(ThemeResourceKeys.RenameActiveBackground) as Brush;
        Brush? activeBorder = Application.Current.TryFindResource(ThemeResourceKeys.RenameActiveBorder) as Brush;
        Brush? fg = Application.Current.TryFindResource(ThemeResourceKeys.EditorForeground) as Brush;

        if (activeBg is not null)
        {
            _renameBox.Background = activeBg;
        }

        if (activeBorder is not null)
        {
            _renameBox.BorderBrush = activeBorder;
        }

        if (fg is not null)
        {
            _renameBox.Foreground = fg;
            _renameBox.CaretBrush = fg;
        }
    }
}
