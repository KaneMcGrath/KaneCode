using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;

namespace KaneCode.Services;

/// <summary>
/// Provides Roslyn-powered Quick Info (hover tooltips) showing type, method, and diagnostic information.
/// </summary>
internal sealed class RoslynQuickInfoService
{
    private readonly RoslynWorkspaceService _roslynService;

    public RoslynQuickInfoService(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;
    }

    /// <summary>
    /// Gets structured quick info at the specified position in the document.
    /// Returns null if no information is available.
    /// </summary>
    public async Task<QuickInfoResult?> GetQuickInfoAsync(
        string filePath,
        int position,
        IReadOnlyList<DiagnosticEntry>? diagnosticEntries = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return null;
        }

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return null;
        }

        var quickInfoService = QuickInfoService.GetService(document);
        if (quickInfoService is null)
        {
            return null;
        }

        var quickInfo = await quickInfoService.GetQuickInfoAsync(
            document, position, cancellationToken).ConfigureAwait(false);

        // Collect diagnostics overlapping the hover position
        var overlappingDiagnostics = FindOverlappingDiagnostics(diagnosticEntries, position);

        if (quickInfo is null && overlappingDiagnostics.Count == 0)
        {
            return null;
        }

        var sections = BuildQuickInfoSections(quickInfo);
        if (sections.Count == 0 && overlappingDiagnostics.Count == 0)
        {
            return null;
        }

        int spanStart = quickInfo?.Span.Start ?? position;
        int spanEnd = quickInfo?.Span.End ?? position;

        return new QuickInfoResult(sections, overlappingDiagnostics, spanStart, spanEnd);
    }

    /// <summary>
    /// Builds structured quick info sections from the Roslyn <see cref="QuickInfoItem"/>.
    /// Each section preserves its tagged text parts with classification information.
    /// </summary>
    internal static IReadOnlyList<QuickInfoSection> BuildQuickInfoSections(QuickInfoItem? quickInfo)
    {
        if (quickInfo is null)
        {
            return [];
        }

        var sections = new List<QuickInfoSection>();

        foreach (var section in quickInfo.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Text))
            {
                continue;
            }

            var taggedParts = new List<QuickInfoTaggedText>();
            foreach (var part in section.TaggedParts)
            {
                taggedParts.Add(new QuickInfoTaggedText(part.Tag, part.Text));
            }

            if (taggedParts.Count > 0)
            {
                sections.Add(new QuickInfoSection(section.Kind, taggedParts));
            }
        }

        return sections;
    }

    /// <summary>
    /// Returns diagnostics whose span overlaps the given position.
    /// </summary>
    internal static IReadOnlyList<DiagnosticEntry> FindOverlappingDiagnostics(
        IReadOnlyList<DiagnosticEntry>? diagnosticEntries,
        int position)
    {
        if (diagnosticEntries is null || diagnosticEntries.Count == 0)
        {
            return [];
        }

        var result = new List<DiagnosticEntry>();
        foreach (var entry in diagnosticEntries)
        {
            if (position >= entry.Start && position <= entry.End)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Maps a Roslyn <see cref="TaggedText"/> tag to a theme resource key for syntax coloring.
    /// Returns null for tags that should use the default tooltip foreground.
    /// </summary>
    internal static string? GetThemeKeyForTag(string tag)
    {
        return tag switch
        {
            TextTags.Keyword => Theming.ThemeResourceKeys.SyntaxKeywordForeground,
            TextTags.Class or TextTags.Record or TextTags.RecordStruct
                => Theming.ThemeResourceKeys.RoslynTypeForeground,
            TextTags.Struct => Theming.ThemeResourceKeys.RoslynTypeForeground,
            TextTags.Interface => Theming.ThemeResourceKeys.RoslynInterfaceForeground,
            TextTags.Enum => Theming.ThemeResourceKeys.RoslynEnumForeground,
            TextTags.EnumMember => Theming.ThemeResourceKeys.RoslynEnumMemberForeground,
            TextTags.Delegate => Theming.ThemeResourceKeys.RoslynDelegateForeground,
            TextTags.TypeParameter => Theming.ThemeResourceKeys.RoslynTypeParameterForeground,
            TextTags.Method or TextTags.ExtensionMethod
                => Theming.ThemeResourceKeys.RoslynMethodForeground,
            TextTags.Property => Theming.ThemeResourceKeys.RoslynPropertyForeground,
            TextTags.Event => Theming.ThemeResourceKeys.RoslynEventForeground,
            TextTags.Field or TextTags.Constant
                => Theming.ThemeResourceKeys.RoslynFieldForeground,
            TextTags.Parameter => Theming.ThemeResourceKeys.RoslynParameterForeground,
            TextTags.Local or TextTags.RangeVariable
                => Theming.ThemeResourceKeys.RoslynLocalForeground,
            TextTags.Namespace => Theming.ThemeResourceKeys.RoslynNamespaceForeground,
            TextTags.StringLiteral => Theming.ThemeResourceKeys.SyntaxStringForeground,
            TextTags.NumericLiteral => Theming.ThemeResourceKeys.SyntaxNumberForeground,
            TextTags.Operator => Theming.ThemeResourceKeys.RoslynOperatorOverloadForeground,
            TextTags.Label => Theming.ThemeResourceKeys.RoslynLabelForeground,
            _ => null
        };
    }
}

/// <summary>
/// Represents the structured result of a quick info lookup for display in a tooltip.
/// </summary>
internal sealed record QuickInfoResult(
    IReadOnlyList<QuickInfoSection> Sections,
    IReadOnlyList<DiagnosticEntry> Diagnostics,
    int SpanStart,
    int SpanEnd)
{
    /// <summary>
    /// Gets a plain text representation of the quick info for accessibility and copy support.
    /// </summary>
    public string ToPlainText()
    {
        var parts = new List<string>();

        foreach (var section in Sections)
        {
            var sectionText = string.Concat(section.TaggedParts.Select(p => p.Text));
            if (!string.IsNullOrWhiteSpace(sectionText))
            {
                parts.Add(sectionText);
            }
        }

        foreach (var diag in Diagnostics)
        {
            parts.Add($"{diag.Severity}: {diag.Id}: {diag.Message}");
        }

        return string.Join(Environment.NewLine, parts);
    }
}

/// <summary>
/// Represents a section within quick info (e.g. Description, Documentation, TypeParameters).
/// </summary>
internal sealed record QuickInfoSection(
    string Kind,
    IReadOnlyList<QuickInfoTaggedText> TaggedParts);

/// <summary>
/// Represents a tagged text part within a quick info section, preserving the classification tag.
/// </summary>
internal sealed record QuickInfoTaggedText(string Tag, string Text);
