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

        Solution solution = document.Project.Solution;
        var symbolName = symbol.Name;
        var referencedSymbols = await SymbolFinder.FindReferencesAsync(
            symbol, solution, cancellationToken).ConfigureAwait(false);

        HashSet<(string FilePath, int StartOffset, ReferenceKind Kind)> seen = new();
        var results = new List<ReferenceItem>();

        foreach (var referencedSymbol in referencedSymbols)
        {
            foreach (Location definitionLocation in referencedSymbol.Definition.Locations)
            {
                if (!definitionLocation.IsInSource || definitionLocation.SourceTree is null)
                {
                    continue;
                }

                ReferenceItem? item = await BuildReferenceItemAsync(
                    symbolName,
                    solution,
                    definitionLocation.SourceSpan,
                    definitionLocation.SourceTree,
                    ReferenceKind.Definition,
                    cancellationToken)
                    .ConfigureAwait(false);

                if (item is not null && seen.Add((item.FilePath, item.StartOffset, item.Kind)))
                {
                    results.Add(item);
                }
            }

            foreach (ReferenceLocation referenceLocation in referencedSymbol.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Location loc = referenceLocation.Location;
                if (!loc.IsInSource || loc.SourceTree is null)
                {
                    continue;
                }

                ReferenceItem? item = await BuildReferenceItemAsync(
                    symbolName,
                    solution,
                    loc.SourceSpan,
                    loc.SourceTree,
                    ReferenceKind.Reference,
                    cancellationToken)
                    .ConfigureAwait(false);

                if (item is not null && seen.Add((item.FilePath, item.StartOffset, item.Kind)))
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

    /// <summary>
    /// Finds source implementations for the symbol at the given position.
    /// </summary>
    public async Task<IReadOnlyList<ReferenceItem>> FindImplementationsAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        var (document, symbol) = await GetDocumentAndSymbolAsync(filePath, position, cancellationToken).ConfigureAwait(false);
        if (document is null || symbol is null)
        {
            return [];
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(
            symbol,
            document.Project.Solution,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await BuildSymbolReferenceItemsAsync(
            symbol.Name,
            implementations,
            ReferenceKind.Implementation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds derived classes for the type symbol at the given position.
    /// </summary>
    public async Task<IReadOnlyList<ReferenceItem>> FindDerivedTypesAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        var (document, symbol) = await GetDocumentAndSymbolAsync(filePath, position, cancellationToken).ConfigureAwait(false);
        if (document is null || symbol is not INamedTypeSymbol typeSymbol)
        {
            return [];
        }

        var derivedClasses = await SymbolFinder.FindDerivedClassesAsync(
            typeSymbol,
            document.Project.Solution,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await BuildSymbolReferenceItemsAsync(
            typeSymbol.Name,
            derivedClasses,
            ReferenceKind.Implementation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source declarations whose names match the provided search text.
    /// </summary>
    public async Task<IReadOnlyList<ReferenceItem>> SearchSymbolsAsync(
        string searchText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchText);

        Solution solution = _roslynService.Workspace.CurrentSolution;
        List<ISymbol> symbols = [];

        foreach (Project project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            IEnumerable<ISymbol> projectSymbols = await SymbolFinder.FindSourceDeclarationsAsync(
                project,
                name => name.Contains(searchText, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.TypeAndMember | SymbolFilter.Namespace,
                cancellationToken).ConfigureAwait(false);

            symbols.AddRange(projectSymbols);
        }

        return await BuildSymbolReferenceItemsAsync(
            searchText,
            symbols,
            ReferenceKind.Definition,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(Document? Document, ISymbol? Symbol)> GetDocumentAndSymbolAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return (null, null);
        }

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return (null, null);
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);

        if (symbol is IAliasSymbol aliasSymbol)
        {
            symbol = aliasSymbol.Target;
        }

        return (document, symbol);
    }

    private static async Task<IReadOnlyList<ReferenceItem>> BuildSymbolReferenceItemsAsync(
        string symbolName,
        IEnumerable<ISymbol> symbols,
        ReferenceKind kind,
        CancellationToken cancellationToken)
    {
        HashSet<(string FilePath, int Start, ReferenceKind Kind)> seen = new();
        List<ReferenceItem> results = [];

        foreach (ISymbol symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (Location loc in symbol.Locations)
            {
                if (!loc.IsInSource || loc.SourceTree is null)
                {
                    continue;
                }

                string? refFilePath = loc.SourceTree.FilePath;
                if (string.IsNullOrWhiteSpace(refFilePath))
                {
                    continue;
                }

                if (!seen.Add((refFilePath, loc.SourceSpan.Start, kind)))
                {
                    continue;
                }

                string displayName = kind == ReferenceKind.Definition
                    ? symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    : symbolName;

                ReferenceItem? item = await BuildReferenceItemAsync(
                    displayName,
                    symbol.ContainingAssembly,
                    loc.SourceSpan,
                    loc.SourceTree,
                    kind,
                    cancellationToken).ConfigureAwait(false);

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
        Solution solution,
        TextSpan span,
        Microsoft.CodeAnalysis.SyntaxTree syntaxTree,
        ReferenceKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);

        string? refFilePath = syntaxTree.FilePath;
        if (string.IsNullOrWhiteSpace(refFilePath))
        {
            return null;
        }

        SourceText sourceText = await syntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        LinePosition linePosition = sourceText.Lines.GetLinePosition(span.Start);
        int line = linePosition.Line + 1;
        int column = linePosition.Character + 1;

        string preview = string.Empty;
        if (linePosition.Line >= 0 && linePosition.Line < sourceText.Lines.Count)
        {
            preview = sourceText.Lines[linePosition.Line].ToString().Trim();
        }

        Document? document = solution.GetDocument(syntaxTree);
        string projectName = document?.Project.Name ?? string.Empty;

        return new ReferenceItem(
            symbolName,
            refFilePath,
            Path.GetFileName(refFilePath),
            projectName,
            line,
            column,
            span.Start,
            preview,
            kind);
    }

    private static async Task<ReferenceItem?> BuildReferenceItemAsync(
        string symbolName,
        IAssemblySymbol? containingAssembly,
        TextSpan span,
        Microsoft.CodeAnalysis.SyntaxTree syntaxTree,
        ReferenceKind kind,
        CancellationToken cancellationToken)
    {
        string? refFilePath = syntaxTree.FilePath;
        if (string.IsNullOrWhiteSpace(refFilePath))
        {
            return null;
        }

        SourceText sourceText = await syntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        LinePosition linePosition = sourceText.Lines.GetLinePosition(span.Start);
        int line = linePosition.Line + 1;
        int column = linePosition.Character + 1;

        string preview = string.Empty;
        if (linePosition.Line >= 0 && linePosition.Line < sourceText.Lines.Count)
        {
            preview = sourceText.Lines[linePosition.Line].ToString().Trim();
        }

        string projectName = containingAssembly?.Name ?? string.Empty;

        return new ReferenceItem(
            symbolName,
            refFilePath,
            Path.GetFileName(refFilePath),
            projectName,
            line,
            column,
            span.Start,
            preview,
            kind);
    }
}

/// <summary>
/// Represents a source location for editor navigation.
/// </summary>
internal sealed record NavigationTarget(string FilePath, int Offset);
