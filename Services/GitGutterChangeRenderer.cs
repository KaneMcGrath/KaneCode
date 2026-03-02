using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System.Windows;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// Draws left-edge gutter markers for line changes relative to HEAD.
/// </summary>
internal sealed class GitGutterChangeRenderer : IBackgroundRenderer
{
    private static readonly Brush s_addedBrush = new SolidColorBrush(Color.FromRgb(115, 201, 145));
    private static readonly Brush s_modifiedBrush = new SolidColorBrush(Color.FromRgb(86, 156, 214));
    private static readonly Brush s_deletedBrush = new SolidColorBrush(Color.FromRgb(244, 71, 71));

    private IReadOnlyList<GitLineChange> _changes = [];

    static GitGutterChangeRenderer()
    {
        if (s_addedBrush.CanFreeze) s_addedBrush.Freeze();
        if (s_modifiedBrush.CanFreeze) s_modifiedBrush.Freeze();
        if (s_deletedBrush.CanFreeze) s_deletedBrush.Freeze();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void UpdateChanges(IReadOnlyList<GitLineChange> changes)
    {
        _changes = changes;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_changes.Count == 0)
        {
            return;
        }

        var document = textView.Document;
        if (document is null)
        {
            return;
        }

        foreach (var change in _changes)
        {
            if (change.LineNumber < 1 || change.LineNumber > document.LineCount)
            {
                continue;
            }

            var line = document.GetLineByNumber(change.LineNumber);
            var segment = new TextSegment { StartOffset = line.Offset, EndOffset = Math.Max(line.EndOffset, line.Offset + 1) };

            var rect = BackgroundGeometryBuilder.GetRectsForSegment(textView, segment).FirstOrDefault();
            if (rect.IsEmpty)
            {
                continue;
            }

            var markerRect = new Rect(rect.Left, rect.Top, 3, rect.Height);
            drawingContext.DrawRectangle(GetBrush(change.ChangeType), null, markerRect);
        }
    }

    private static Brush GetBrush(GitLineChangeType changeType) => changeType switch
    {
        GitLineChangeType.Added => s_addedBrush,
        GitLineChangeType.Deleted => s_deletedBrush,
        _ => s_modifiedBrush
    };
}

internal enum GitLineChangeType
{
    Added,
    Modified,
    Deleted
}

internal sealed record GitLineChange(int LineNumber, GitLineChangeType ChangeType);
