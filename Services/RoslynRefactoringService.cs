using KaneCode.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace KaneCode.Services;

/// <summary>
/// Provides Roslyn-powered refactoring operations: inline rename and
/// symbol information for rename eligibility checks.
/// Extract method / move type are surfaced through <see cref="RoslynCodeActionService"/>
/// as code refactoring providers.
/// </summary>
internal sealed class RoslynRefactoringService
{
    private readonly RoslynWorkspaceService _roslynService;

    public RoslynRefactoringService(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;
    }

    /// <summary>
    /// Renames the symbol at the given position across the entire solution.
    /// Returns changed, added, and removed files, or null if rename is not possible.
    /// </summary>
    public async Task<SolutionEditResult?> RenameSymbolAsync(
        string filePath,
        int position,
        string newName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

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
            document, position, cancellationToken).ConfigureAwait(false);

        if (symbol is null || !CanRename(symbol))
        {
            return null;
        }

        Solution solution = document.Project.Solution;
        SymbolRenameOptions options = default;

        Solution newSolution = await Renamer.RenameSymbolAsync(
            solution, symbol, options, newName, cancellationToken).ConfigureAwait(false);

        SolutionEditResult result = await SolutionEditResult.CollectFromSolutionChangesAsync(
            solution, newSolution, cancellationToken).ConfigureAwait(false);

        return result.IsEmpty ? null : result;
    }

    /// <summary>
    /// Gets the name and span of the symbol at the given position, if it is renamable.
    /// Used to pre-populate the rename prompt.
    /// </summary>
    public async Task<SymbolNameInfo?> GetSymbolNameAtPositionAsync(
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
            document, position, cancellationToken).ConfigureAwait(false);

        if (symbol is null || !CanRename(symbol))
        {
            return null;
        }

        return new SymbolNameInfo(symbol.Name);
    }

    private static bool CanRename(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
        {
            return false;
        }

        // Only rename symbols that have source locations (skip metadata).
        return symbol.Locations.Any(static l => l.IsInSource);
    }
}

/// <summary>
/// Information about a symbol at a position, used to pre-populate the rename dialog.
/// </summary>
internal sealed record SymbolNameInfo(string Name);
