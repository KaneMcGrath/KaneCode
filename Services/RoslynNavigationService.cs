using KaneCode.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using System.IO;

namespace KaneCode.Services;

/// <summary>
/// Provides Roslyn-powered symbol navigation (Go to Definition, Find References).
/// </summary>
internal sealed class RoslynNavigationService
{
    private readonly RoslynWorkspaceService _roslynService;

    public RoslynNavigationService(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;
    }

    /// <summary>
    /// Finds the source definition target for the symbol at the given position.
    /// </summary>
    public async Task<NavigationTarget?> FindDefinitionAsync(
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

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);

        if (symbol is null)
        {
            return null;
        }

        if (symbol is IAliasSymbol aliasSymbol)
        {
            symbol = aliasSymbol.Target;
        }

        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(
            symbol,
            document.Project.Solution,
            cancellationToken).ConfigureAwait(false) ?? symbol;

        var location = sourceSymbol.Locations.FirstOrDefault(static l => l.IsInSource);
        if (location?.SourceTree is null)
        {
            return null;
        }

        var targetDocument = document.Project.Solution.GetDocument(location.SourceTree);
        var targetFilePath = targetDocument?.FilePath ?? location.SourceTree.FilePath;
        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            return null;
        }

        return new NavigationTarget(targetFilePath, location.SourceSpan.Start);
    }

    /// <summary>
    /// Finds all references to the symbol at the given position across the solution.
    /// Returns an empty list when no symbol is found at the position.
    /// </summary>
    public async Task<IReadOnlyList<ReferenceItem>> FindReferencesAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return [];
        }

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return [];
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            document, position, cancellationToken).ConfigureAwait(false);

        if (symbol is null)
        {
            return [];
        }

        if (symbol is IAliasSymbol aliasSymbol)
        {
            symbol = aliasSymbol.Target;
        }

        var symbolName = symbol.Name;
        var referencedSymbols = await SymbolFinder.FindReferencesAsync(
            symbol, document.Project.Solution, cancellationToken).ConfigureAwait(false);

        var results = new List<ReferenceItem>();

        foreach (var referencedSymbol in referencedSymbols)
        {
            // Include the definition itself
            foreach (var loc in referencedSymbol.Definition.Locations)
            {
                if (!loc.IsInSource || loc.SourceTree is null)
                {
                    continue;
                }

                var refFilePath = loc.SourceTree.FilePath;
                var item = await BuildReferenceItemAsync(
                    symbolName, refFilePath, loc.SourceSpan, loc.SourceTree, cancellationToken)
                    .ConfigureAwait(false);

                if (item is not null)
                {
                    results.Add(item);
                }
            }

            // Include all usage references
            foreach (var location in referencedSymbol.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var loc = location.Location;
                if (!loc.IsInSource || loc.SourceTree is null)
                {
                    continue;
                }

                var refFilePath = loc.SourceTree.FilePath;
                var item = await BuildReferenceItemAsync(
                    symbolName, refFilePath, loc.SourceSpan, loc.SourceTree, cancellationToken)
                    .ConfigureAwait(false);

                if (item is not null)
                {
                    results.Add(item);
                }
            }
        }

        return results
            .OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Column)
            .ToList();
    }

    private static async Task<ReferenceItem?> BuildReferenceItemAsync(
        string symbolName,
        string refFilePath,
        TextSpan span,
        Microsoft.CodeAnalysis.SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refFilePath))
        {
            return null;
        }

        var sourceText = await syntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var linePosition = sourceText.Lines.GetLinePosition(span.Start);
        var line = linePosition.Line + 1;
        var column = linePosition.Character + 1;

        var preview = string.Empty;
        if (linePosition.Line >= 0 && linePosition.Line < sourceText.Lines.Count)
        {
            preview = sourceText.Lines[linePosition.Line].ToString().Trim();
        }

        return new ReferenceItem(
            symbolName,
            refFilePath,
            Path.GetFileName(refFilePath),
            line,
            column,
            span.Start,
            preview);
    }
}

/// <summary>
/// Represents a source location for editor navigation.
/// </summary>
internal sealed record NavigationTarget(string FilePath, int Offset);
