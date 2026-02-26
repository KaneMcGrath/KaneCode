using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using KaneCode.Theming;
using Microsoft.CodeAnalysis;
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

        _classifiedSpans = spans.ToList();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_classifiedSpans.Count == 0)
        {
            return;
        }

        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        foreach (var span in _classifiedSpans)
        {
            // Skip spans outside this line
            if (span.TextSpan.End <= lineStart || span.TextSpan.Start >= lineEnd)
            {
                continue;
            }

            var brush = GetBrushForClassification(span.ClassificationType);
            if (brush is null)
            {
                continue;
            }

            var start = Math.Max(span.TextSpan.Start, lineStart);
            var end = Math.Min(span.TextSpan.End, lineEnd);

            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
            });
        }
    }

    private static Brush? GetBrushForClassification(string classificationType)
    {
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

        if (resourceKey is null)
        {
            return null;
        }

        return Application.Current.TryFindResource(resourceKey) as Brush;
    }
}
