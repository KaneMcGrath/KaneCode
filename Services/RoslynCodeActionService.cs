using KaneCode.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;

namespace KaneCode.Services;

/// <summary>
/// Surfaces Roslyn code fixes and refactorings at a given editor position,
/// and applies selected code actions back to the workspace.
/// </summary>
internal sealed class RoslynCodeActionService
{
    private readonly RoslynWorkspaceService _roslynService;
    private readonly Lazy<IReadOnlyList<CodeFixProvider>> _codeFixProviders;
    private readonly Lazy<IReadOnlyList<CodeRefactoringProvider>> _codeRefactoringProviders;

    public RoslynCodeActionService(RoslynWorkspaceService roslynService)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;

        _codeFixProviders = new Lazy<IReadOnlyList<CodeFixProvider>>(DiscoverCodeFixProviders);
        _codeRefactoringProviders = new Lazy<IReadOnlyList<CodeRefactoringProvider>>(DiscoverCodeRefactoringProviders);
    }

    /// <summary>
    /// Gets all available code actions (fixes + refactorings) at the specified position.
    /// </summary>
    public async Task<IReadOnlyList<CodeActionItem>> GetCodeActionsAsync(
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

        var results = new List<CodeActionItem>();

        // Collect code fixes for diagnostics at/near the position
        await CollectCodeFixesAsync(document, position, results, cancellationToken).ConfigureAwait(false);

        // Collect refactorings at the position
        await CollectRefactoringsAsync(document, position, results, cancellationToken).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Gets code actions focused on generating missing members at the specified position.
    /// </summary>
    public async Task<IReadOnlyList<CodeActionItem>> GetGenerateMissingMembersActionsAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        var actions = await GetCodeActionsAsync(filePath, position, cancellationToken).ConfigureAwait(false);

        return actions
            .Where(static action => IsGenerateMissingMembersAction(action.Title))
            .ToList();
    }

    /// <summary>
    /// Applies a code action and returns the new text for the affected document.
    /// Returns null if the action could not be applied.
    /// </summary>
    public async Task<ApplyResult?> ApplyCodeActionAsync(
        string filePath,
        CodeAction codeAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(codeAction);

        SolutionEditResult? multiResult = await ApplyCodeActionMultiFileAsync(filePath, codeAction, cancellationToken)
            .ConfigureAwait(false);

        if (multiResult is null)
        {
            return null;
        }

        // Return the text for the originating document (backward-compatible).
        if (multiResult.ChangedFiles.TryGetValue(filePath, out string? newText))
        {
            return new ApplyResult(newText);
        }

        // Fallback: return the first changed file's text.
        string? first = multiResult.ChangedFiles.Values.FirstOrDefault();
        return first is not null ? new ApplyResult(first) : null;
    }

    /// <summary>
    /// Applies a code action and returns all changed, added, and removed files.
    /// Returns null if the action could not be applied.
    /// </summary>
    public async Task<SolutionEditResult?> ApplyCodeActionMultiFileAsync(
        string filePath,
        CodeAction codeAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(codeAction);

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return null;
        }

        var operations = await codeAction.GetOperationsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var operation in operations)
        {
            if (operation is ApplyChangesOperation applyChanges)
            {
                var changedSolution = applyChanges.ChangedSolution;
                var originalSolution = document.Project.Solution;

                var result = await SolutionEditResult.CollectFromSolutionChangesAsync(
                        originalSolution, changedSolution, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.IsEmpty)
                {
                    return result;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets code fixes for a specific diagnostic at the given position.
    /// Used by the error list "fix" links to target a specific diagnostic ID.
    /// </summary>
    public async Task<IReadOnlyList<CodeActionItem>> GetCodeFixesForDiagnosticAsync(
        string filePath,
        int position,
        string diagnosticId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticId);

        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return [];
        }

        var document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return [];
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return [];
        }

        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int clampedPosition = Math.Min(position, sourceText.Length);
        var span = TextSpan.FromBounds(clampedPosition, Math.Min(clampedPosition + 1, sourceText.Length));

        var allDiagnostics = semanticModel.GetDiagnostics(span, cancellationToken);
        var targetDiagnostics = allDiagnostics
            .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.Ordinal))
            .ToImmutableArray();

        if (targetDiagnostics.IsEmpty)
        {
            // Fall back to the whole line in case the span was too narrow
            var line = sourceText.Lines.GetLineFromPosition(clampedPosition);
            allDiagnostics = semanticModel.GetDiagnostics(line.Span, cancellationToken);
            targetDiagnostics = allDiagnostics
                .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.Ordinal))
                .ToImmutableArray();
        }

        if (targetDiagnostics.IsEmpty)
        {
            return [];
        }

        var results = new List<CodeActionItem>();
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        var providers = _codeFixProviders.Value;

        foreach (var provider in providers)
        {
            var fixableIds = provider.FixableDiagnosticIds;
            var matching = targetDiagnostics.Where(d => fixableIds.Contains(d.Id)).ToImmutableArray();
            if (matching.IsEmpty)
            {
                continue;
            }

            foreach (var diagnostic in matching)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var context = new CodeFixContext(
                        document,
                        diagnostic,
                        (action, _) => AddLeafActions(action, results, seenTitles),
                        cancellationToken);

                    await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Some providers may fail; skip them.
                }
            }
        }

        return results;
    }

    private async Task CollectCodeFixesAsync(
        Document document,
        int position,
        List<CodeActionItem> results,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return;
        }

        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var line = sourceText.Lines.GetLineFromPosition(Math.Min(position, sourceText.Length));
        var lineSpan = line.Span;

        // Get diagnostics that overlap the current line
        var allDiagnostics = semanticModel.GetDiagnostics(lineSpan, cancellationToken);
        if (allDiagnostics.IsEmpty)
        {
            return;
        }

        var providers = _codeFixProviders.Value;
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            var fixableIds = provider.FixableDiagnosticIds;
            var matching = allDiagnostics.Where(d => fixableIds.Contains(d.Id)).ToImmutableArray();

            if (matching.IsEmpty)
            {
                continue;
            }

            foreach (var diagnostic in matching)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var context = new CodeFixContext(
                        document,
                        diagnostic,
                        (action, _) => AddLeafActions(action, results, seenTitles),
                        cancellationToken);

                    await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Some providers may fail for certain diagnostics; skip them silently.
                }
            }
        }
    }

    private async Task CollectRefactoringsAsync(
        Document document,
        int position,
        List<CodeActionItem> results,
        CancellationToken cancellationToken)
    {
        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var clampedPosition = Math.Min(position, sourceText.Length);
        var span = TextSpan.FromBounds(clampedPosition, clampedPosition);

        var providers = _codeRefactoringProviders.Value;
        var seenTitles = new HashSet<string>(results.Select(r => r.Title), StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var context = new CodeRefactoringContext(
                    document,
                    span,
                    action => AddLeafActions(action, results, seenTitles),
                    cancellationToken);

                await provider.ComputeRefactoringsAsync(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Some providers may fail; skip them silently.
            }
        }
    }

    /// <summary>
    /// Recursively flattens a code action: if it has nested sub-actions, each leaf
    /// is added individually so the user picks a directly-applicable action.
    /// Parent actions with nested children throw <see cref="NotSupportedException"/>
    /// when <see cref="CodeAction.GetOperationsAsync"/> is called.
    /// </summary>
    private static void AddLeafActions(
        CodeAction action,
        List<CodeActionItem> results,
        HashSet<string> seenTitles)
    {
        var nested = action.NestedActions;
        if (!nested.IsDefault && nested.Length > 0)
        {
            foreach (var child in nested)
            {
                AddLeafActions(child, results, seenTitles);
            }

            return;
        }

        if (seenTitles.Add(action.Title))
        {
            results.Add(new CodeActionItem(action.Title, action));
        }
    }

    private static bool IsGenerateMissingMembersAction(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return title.Contains("Generate method", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Generate constructor", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Generate property", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Generate field", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Generate class", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Generate type", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Implement interface", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Implement abstract class", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CodeFixProvider> DiscoverCodeFixProviders()
    {
        var providers = new List<CodeFixProvider>();
        foreach (var assembly in MefHostServices.DefaultAssemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    var attr = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                    if (attr is null || !attr.Languages.Contains(LanguageNames.CSharp))
                    {
                        continue;
                    }

                    if (Activator.CreateInstance(type) is CodeFixProvider provider)
                    {
                        providers.Add(provider);
                    }
                }
            }
            catch
            {
                // Some assemblies may not load all types; skip them.
            }
        }

        Debug.WriteLine($"Discovered {providers.Count} C# CodeFixProviders");
        return providers;
    }

    private static IReadOnlyList<CodeRefactoringProvider> DiscoverCodeRefactoringProviders()
    {
        var providers = new List<CodeRefactoringProvider>();
        foreach (var assembly in MefHostServices.DefaultAssemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || !typeof(CodeRefactoringProvider).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    var attr = type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>();
                    if (attr is null || !attr.Languages.Contains(LanguageNames.CSharp))
                    {
                        continue;
                    }

                    if (Activator.CreateInstance(type) is CodeRefactoringProvider provider)
                    {
                        providers.Add(provider);
                    }
                }
            }
            catch
            {
                // Some assemblies may not load all types; skip them.
            }
        }

        Debug.WriteLine($"Discovered {providers.Count} C# CodeRefactoringProviders");
        return providers;
    }

    /// <summary>
    /// Result of applying a code action.
    /// </summary>
    public sealed record ApplyResult(string NewText);

    }
