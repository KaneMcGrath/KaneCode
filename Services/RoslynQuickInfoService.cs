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
    /// Gets quick info text at the specified position in the document.
    /// Returns null if no information is available.
    /// </summary>
    public async Task<QuickInfoResult?> GetQuickInfoAsync(
        string filePath,
        int position,
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

        if (quickInfo is null)
        {
            return null;
        }

        var text = BuildQuickInfoText(quickInfo);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new QuickInfoResult(
            text,
            quickInfo.Span.Start,
            quickInfo.Span.End);
    }

    /// <summary>
    /// Extracts readable text from the Roslyn <see cref="QuickInfoItem"/> tagged sections.
    /// </summary>
    private static string BuildQuickInfoText(QuickInfoItem quickInfo)
    {
        var parts = new List<string>();

        foreach (var section in quickInfo.Sections)
        {
            var sectionText = section.Text;
            if (!string.IsNullOrWhiteSpace(sectionText))
            {
                parts.Add(sectionText);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }
}

/// <summary>
/// Represents the result of a quick info lookup for display in a tooltip.
/// </summary>
internal sealed record QuickInfoResult(string Text, int SpanStart, int SpanEnd);
