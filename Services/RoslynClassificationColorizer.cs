using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using KaneCode.Theming;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using System.Windows;
using System.Windows.Media;

namespace KaneCode.Services;

/// <summary>
/// An AvalonEdit <see cref="DocumentColorizingTransformer"/> that uses Roslyn's
/// semantic classification to colorize types, methods, parameters, and other
/// semantic constructs beyond what regex-based highlighting can achieve.
/// </summary>
internal sealed class RoslynClassificationColorizer : DocumentColorizingTransformer
{
    private readonly RoslynWorkspaceService _roslynService;
    private string? _filePath;
    private IReadOnlyList<ClassifiedSpan> _classifiedSpans = [];

    /// <summary>
    /// Caches resolved theme brushes per classification type so per-line colorization
    /// does not perform a WPF resource lookup for every span on every line.
    /// </summary>
    private static readonly Dictionary<string, Brush?> s_brushCache = new(StringComparer.Ordinal);

    public RoslynClassificationColorizer(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;
    }

    /// <summary>
    /// Sets the file path for the currently active document.
    /// </summary>
    public string? FilePath
    {
        get => _filePath;
        set => _filePath = value;
    }

    /// <summary>
    /// Clears cached classifications and updates the active file path.
    /// </summary>
    public void Reset(string? filePath = null)
    {
        _filePath = filePath;
        _classifiedSpans = [];
    }

    /// <summary>
    /// Directly sets the classified spans from an external source (e.g. <see cref="BackgroundAnalysisScheduler"/>).
    /// Call on the UI thread, then invalidate visual lines.
    /// </summary>
    public void SetClassifiedSpans(IReadOnlyList<ClassifiedSpan> spans)
    {
        _classifiedSpans = EnsureSorted(spans);
    }

    /// <summary>
    /// Updates the cached classified spans. Call this after the document text changes,
    /// on a background thread, then invalidate the visual lines.
    /// </summary>
    public async Task UpdateClassificationsAsync(CancellationToken cancellationToken = default)
    {
        if (_filePath is null || !RoslynWorkspaceService.IsCSharpFile(_filePath))
        {
            _classifiedSpans = [];
            return;
        }

        var document = _roslynService.GetDocument(_filePath);
        if (document is null)
        {
            _classifiedSpans = [];
            return;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var spans = await Classifier.GetClassifiedSpansAsync(
            document,
            TextSpan.FromBounds(0, text.Length),
            cancellationToken).ConfigureAwait(false);

        _classifiedSpans = EnsureSorted(spans.ToList());
    }

    /// <summary>
    /// Clears the cached theme brushes. Call when the application theme changes so
    /// stale brush references are not reused.
    /// </summary>
    internal static void ClearBrushCache()
    {
        s_brushCache.Clear();
    }

    /// <summary>
    /// Roslyn returns classified spans in document order. The per-line lookup below relies
    /// on that ordering, so defensively sort when the input is unsorted (e.g. test callers).
    /// </summary>
    private static IReadOnlyList<ClassifiedSpan> EnsureSorted(IReadOnlyList<ClassifiedSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        int previousEnd = spans[0].TextSpan.End;
        for (int i = 1; i < spans.Count; i++)
        {
            if (spans[i].TextSpan.Start < previousEnd)
            {
                return spans.OrderBy(s => s.TextSpan.Start).ToList();
            }

            previousEnd = spans[i].TextSpan.End;
        }

        return spans;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var spans = _classifiedSpans;
        if (spans.Count == 0)
        {
            return;
        }

        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        // Spans are ordered by document position, so binary search for the first span
        // that could overlap this line, then walk forward until spans pass the line end.
        // This reduces the per-line cost from O(total spans) to O(log n + overlapping spans),
        // which matters a lot for large files where span lists can be tens of thousands long.
        int index = FindFirstSpanAtOrAfter(lineStart, spans);
        for (int i = index; i < spans.Count; i++)
        {
            var span = spans[i];
            if (span.TextSpan.Start >= lineEnd)
            {
                break;
            }

            var brush = GetBrushForClassification(span.ClassificationType);
            if (brush is null)
            {
                continue;
            }

            var start = Math.Max(span.TextSpan.Start, lineStart);
            var end = Math.Min(span.TextSpan.End, lineEnd);
            if (start >= end)
            {
                continue;
            }

            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
            });
        }
    }

    /// <summary>
    /// Finds the index of the first span whose <see cref="ClassifiedSpan.TextSpan"/> ends
    /// after <paramref name="lineStart"/> (i.e. the first span that can intersect a line
    /// starting at that offset). Returns <c>spans.Count</c> when no span qualifies.
    /// </summary>
    private static int FindFirstSpanAtOrAfter(int lineStart, IReadOnlyList<ClassifiedSpan> spans)
    {
        int low = 0;
        int high = spans.Count - 1;
        int result = spans.Count;

        while (low <= high)
        {
            int mid = (low + high) >> 1;
            if (spans[mid].TextSpan.End > lineStart)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result;
    }

    private static Brush? GetBrushForClassification(string classificationType)
    {
        if (s_brushCache.TryGetValue(classificationType, out Brush? cached))
        {
            return cached;
        }

        var resourceKey = classificationType switch
        {
            ClassificationTypeNames.ClassName
                or ClassificationTypeNames.StructName
                or ClassificationTypeNames.RecordClassName
                or ClassificationTypeNames.RecordStructName => ThemeResourceKeys.RoslynTypeForeground,

            ClassificationTypeNames.InterfaceName => ThemeResourceKeys.RoslynInterfaceForeground,

            ClassificationTypeNames.EnumName => ThemeResourceKeys.RoslynEnumForeground,

            ClassificationTypeNames.EnumMemberName => ThemeResourceKeys.RoslynEnumMemberForeground,

            ClassificationTypeNames.DelegateName => ThemeResourceKeys.RoslynDelegateForeground,

            ClassificationTypeNames.TypeParameterName => ThemeResourceKeys.RoslynTypeParameterForeground,

            ClassificationTypeNames.MethodName
                or ClassificationTypeNames.ExtensionMethodName => ThemeResourceKeys.RoslynMethodForeground,

            ClassificationTypeNames.PropertyName => ThemeResourceKeys.RoslynPropertyForeground,

            ClassificationTypeNames.EventName => ThemeResourceKeys.RoslynEventForeground,

            ClassificationTypeNames.FieldName
                or ClassificationTypeNames.ConstantName => ThemeResourceKeys.RoslynFieldForeground,

            ClassificationTypeNames.ParameterName => ThemeResourceKeys.RoslynParameterForeground,

            ClassificationTypeNames.LocalName => ThemeResourceKeys.RoslynLocalForeground,

            ClassificationTypeNames.NamespaceName => ThemeResourceKeys.RoslynNamespaceForeground,

            ClassificationTypeNames.Keyword
                or ClassificationTypeNames.ControlKeyword => ThemeResourceKeys.RoslynControlKeywordForeground,

            ClassificationTypeNames.StringEscapeCharacter => ThemeResourceKeys.RoslynStringEscapeForeground,

            ClassificationTypeNames.OperatorOverloaded => ThemeResourceKeys.RoslynOperatorOverloadForeground,

            ClassificationTypeNames.LabelName => ThemeResourceKeys.RoslynLabelForeground,

            _ => null
        };

        Brush? brush = resourceKey is null
            ? null
            : Application.Current.TryFindResource(resourceKey) as Brush;

        // Cache misses too so unknown classification types are resolved once.
        s_brushCache[classificationType] = brush;
        return brush;
    }
}
