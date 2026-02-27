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

        var multiResult = await ApplyCodeActionMultiFileAsync(filePath, codeAction, cancellationToken)
            .ConfigureAwait(false);

        if (multiResult is null)
        {
            return null;
        }

        // Return the text for the originating document (backward-compatible).
        if (multiResult.ChangedFiles.TryGetValue(filePath, out var newText))
        {
            return new ApplyResult(newText);
        }

        // Fallback: return the first changed file's text.
        var first = multiResult.ChangedFiles.Values.FirstOrDefault();
        return first is not null ? new ApplyResult(first) : null;
    }

    /// <summary>
    /// Applies a code action and returns all changed files.
    /// Returns null if the action could not be applied.
    /// </summary>
    public async Task<ApplyMultiFileResult?> ApplyCodeActionMultiFileAsync(
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

                var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var projectChanges in changedSolution.GetChanges(originalSolution).GetProjectChanges())
                {
                    foreach (var docId in projectChanges.GetChangedDocuments())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var changedDoc = changedSolution.GetDocument(docId);
                        if (changedDoc?.FilePath is null)
                        {
                            continue;
                        }

                        var text = await changedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        changes[changedDoc.FilePath] = text.ToString();
                    }
                }

                if (changes.Count > 0)
                {
                    return new ApplyMultiFileResult(changes);
                }
            }
        }

        return null;
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

    /// <summary>
    /// Result of applying a code action that may affect multiple files.
    /// </summary>
    public sealed record ApplyMultiFileResult(IReadOnlyDictionary<string, string> ChangedFiles);
}
