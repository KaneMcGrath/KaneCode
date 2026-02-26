using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace KaneCode.Services;

/// <summary>
/// Provides Roslyn-powered symbol navigation (Go to Definition).
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
}

/// <summary>
/// Represents a source location for editor navigation.
/// </summary>
internal sealed record NavigationTarget(string FilePath, int Offset);
