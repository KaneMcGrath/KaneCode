using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using KaneCode.Theming;
using Microsoft.CodeAnalysis;
using System.Windows;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// An AvalonEdit <see cref="IBackgroundRenderer"/> that draws squiggly underlines
/// for Roslyn diagnostics (errors, warnings, info).
/// </summary>
internal sealed class RoslynDiagnosticRenderer : IBackgroundRenderer
{
    private IReadOnlyList<DiagnosticEntry> _entries = [];

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>
    /// Updates the diagnostics to render.
    /// </summary>
    public void UpdateDiagnostics(IReadOnlyList<DiagnosticEntry> entries)
    {
        _entries = entries;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var document = textView.Document;
        if (document is null)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            if (entry.Start < 0 || entry.End > document.TextLength || entry.Start >= entry.End)
            {
                continue;
            }

            var brush = GetSquigglyBrush(entry.Severity);
            if (brush is null)
            {
                continue;
            }

            var segment = new TextSegment { StartOffset = entry.Start, EndOffset = entry.End };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                DrawSquigglyLine(drawingContext, brush, rect);
            }
        }
    }

    private static void DrawSquigglyLine(DrawingContext context, Brush brush, Rect rect)
    {
        var pen = new Pen(brush, 1.0) { DashStyle = DashStyles.Dot };
        pen.Freeze();

        var y = rect.Bottom;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Left, y), false, false);

            var x = rect.Left + 2;
            var toggle = true;
            while (x < rect.Right)
            {
                ctx.LineTo(new Point(x, y + (toggle ? -2 : 0)), true, false);
                toggle = !toggle;
                x += 2;
            }
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static Brush? GetSquigglyBrush(DiagnosticSeverity severity)
    {
        var key = severity switch
        {
            DiagnosticSeverity.Error => ThemeResourceKeys.DiagnosticErrorForeground,
            DiagnosticSeverity.Warning => ThemeResourceKeys.DiagnosticWarningForeground,
            DiagnosticSeverity.Info => ThemeResourceKeys.DiagnosticInfoForeground,
            _ => null
        };

        if (key is null)
        {
            return null;
        }

        return Application.Current.TryFindResource(key) as Brush;
    }
}

/// <summary>
/// Represents a diagnostic location and severity for rendering.
/// </summary>
internal sealed record DiagnosticEntry(int Start, int End, DiagnosticSeverity Severity, string Message, string Id);
