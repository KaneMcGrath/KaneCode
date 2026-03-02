using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// Displays side-by-side file content for Git diff viewing and highlights changed lines.
/// </summary>
public partial class GitDiffPanel : UserControl
{
    private static readonly Brush s_modifiedBrush = new SolidColorBrush(Color.FromRgb(65, 56, 20));
    private static readonly Brush s_addedBrush = new SolidColorBrush(Color.FromRgb(24, 66, 44));
    private static readonly Brush s_deletedBrush = new SolidColorBrush(Color.FromRgb(82, 27, 27));

    static GitDiffPanel()
    {
        if (s_modifiedBrush.CanFreeze) s_modifiedBrush.Freeze();
        if (s_addedBrush.CanFreeze) s_addedBrush.Freeze();
        if (s_deletedBrush.CanFreeze) s_deletedBrush.Freeze();
    }

    public GitDiffPanel()
    {
        InitializeComponent();
    }

    public void SetDiff(string relativePath, string leftText, string rightText)
    {
        HeaderText.Text = $"Diff: {relativePath}";

        LeftEditor.Text = leftText;
        RightEditor.Text = rightText;

        ApplyLineColorization(leftText, rightText);
    }

    private void ApplyLineColorization(string leftText, string rightText)
    {
        var leftLines = SplitLines(leftText);
        var rightLines = SplitLines(rightText);

        var leftLineBrushes = new Dictionary<int, Brush>();
        var rightLineBrushes = new Dictionary<int, Brush>();

        var maxLines = Math.Max(leftLines.Length, rightLines.Length);
        for (var index = 0; index < maxLines; index++)
        {
            var hasLeft = index < leftLines.Length;
            var hasRight = index < rightLines.Length;

            if (!hasLeft && hasRight)
            {
                rightLineBrushes[index + 1] = s_addedBrush;
                continue;
            }

            if (hasLeft && !hasRight)
            {
                leftLineBrushes[index + 1] = s_deletedBrush;
                continue;
            }

            if (!string.Equals(leftLines[index], rightLines[index], StringComparison.Ordinal))
            {
                leftLineBrushes[index + 1] = s_modifiedBrush;
                rightLineBrushes[index + 1] = s_modifiedBrush;
            }
        }

        LeftEditor.TextArea.TextView.LineTransformers.Clear();
        RightEditor.TextArea.TextView.LineTransformers.Clear();

        LeftEditor.TextArea.TextView.LineTransformers.Add(new DiffLineColorizer(leftLineBrushes));
        RightEditor.TextArea.TextView.LineTransformers.Add(new DiffLineColorizer(rightLineBrushes));

        LeftEditor.TextArea.TextView.Redraw();
        RightEditor.TextArea.TextView.Redraw();
    }

    private static string[] SplitLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private sealed class DiffLineColorizer : DocumentColorizingTransformer
    {
        private readonly IReadOnlyDictionary<int, Brush> _lineBrushes;

        public DiffLineColorizer(IReadOnlyDictionary<int, Brush> lineBrushes)
        {
            _lineBrushes = lineBrushes;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (!_lineBrushes.TryGetValue(line.LineNumber, out var brush))
            {
                return;
            }

            ChangeLinePart(line.Offset, line.EndOffset, visualElement =>
            {
                visualElement.TextRunProperties.SetBackgroundBrush(brush);
            });
        }
    }
}
