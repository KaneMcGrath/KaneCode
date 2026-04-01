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

    /// <summary>
    /// Finds all occurrences of the renamable symbol at the given position within the same file.
    /// Returns the symbol name plus a list of spans suitable for inline rename highlighting,
    /// or null if the symbol is not renamable.
    /// </summary>
    public async Task<InlineRenameInfo?> FindRenameSpansAsync(
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

        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        var solution = document.Project.Solution;
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, solution, cancellationToken).ConfigureAwait(false);

        List<InlineRenameSpan> spans = [];

        // Collect definition locations in this file
        foreach (var refSymbol in references)
        {
            foreach (var loc in refSymbol.Definition.Locations)
            {
                if (!loc.IsInSource || loc.SourceTree is null)
                {
                    continue;
                }

                if (string.Equals(loc.SourceTree.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    spans.Add(new InlineRenameSpan(loc.SourceSpan.Start, loc.SourceSpan.Length, IsDefinition: true));
                }
            }

            // Collect reference locations in this file
            foreach (var refLoc in refSymbol.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var loc = refLoc.Location;
                if (!loc.IsInSource || loc.SourceTree is null)
                {
                    continue;
                }

                if (string.Equals(loc.SourceTree.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    spans.Add(new InlineRenameSpan(loc.SourceSpan.Start, loc.SourceSpan.Length, IsDefinition: false));
                }
            }
        }

        if (spans.Count == 0)
        {
            return null;
        }

        // Deduplicate spans that appear as both definition and reference
        spans = spans
            .GroupBy(s => s.Start)
            .Select(g => g.Any(s => s.IsDefinition)
                ? new InlineRenameSpan(g.Key, g.First().Length, IsDefinition: true)
                : g.First())
            .OrderBy(s => s.Start)
            .ToList();

        return new InlineRenameInfo(symbol.Name, filePath, spans);
    }

    /// <summary>
    /// Gets a preview of all files that would be affected by renaming the symbol
    /// at the given position. Returns empty when no cross-file references exist.
    /// </summary>
    public async Task<IReadOnlyList<RenamePreviewItem>> GetRenamePreviewAsync(
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

        if (symbol is null || !CanRename(symbol))
        {
            return [];
        }

        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        var solution = document.Project.Solution;
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, solution, cancellationToken).ConfigureAwait(false);

        // Group locations by file
        Dictionary<string, int> fileCounts = new(StringComparer.OrdinalIgnoreCase);

        foreach (var refSymbol in references)
        {
            foreach (var loc in refSymbol.Definition.Locations)
            {
                if (loc.IsInSource && loc.SourceTree?.FilePath is not null)
                {
                    string path = loc.SourceTree.FilePath;
                    fileCounts[path] = fileCounts.GetValueOrDefault(path) + 1;
                }
            }

            foreach (var refLoc in refSymbol.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loc = refLoc.Location;
                if (loc.IsInSource && loc.SourceTree?.FilePath is not null)
                {
                    string path = loc.SourceTree.FilePath;
                    fileCounts[path] = fileCounts.GetValueOrDefault(path) + 1;
                }
            }
        }

        return fileCounts
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new RenamePreviewItem(kv.Key, System.IO.Path.GetFileName(kv.Key), kv.Value))
            .ToList();
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

/// <summary>
/// Information gathered for starting an inline rename session: the symbol name,
/// the file it's in, and all occurrence spans within that file.
/// </summary>
internal sealed record InlineRenameInfo(
    string SymbolName,
    string FilePath,
    IReadOnlyList<InlineRenameSpan> Spans);

/// <summary>
/// Represents a file affected by a rename, with the count of occurrences for preview display.
/// </summary>
internal sealed record RenamePreviewItem(string FilePath, string FileName, int OccurrenceCount);
