using KaneCode.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace KaneCode.Services;

/// <summary>
/// Surfaces Roslyn code fixes and refactorings at a given editor position,
/// and applies selected code actions back to the workspace.
/// </summary>
internal sealed class RoslynCodeActionService
{
    private const int MaxProviderFailureLogEntries = 200;

    private readonly RoslynWorkspaceService _roslynService;
    private readonly Lazy<IReadOnlyList<CodeFixProvider>> _codeFixProviders;
    private readonly Lazy<IReadOnlyList<CodeRefactoringProvider>> _codeRefactoringProviders;
    private readonly ConcurrentDictionary<string, ProviderInvocationCounters> _providerInvocationCounters = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<CodeActionProviderFailureLogEntry> _providerFailureLogEntries = new();

    public RoslynCodeActionService(RoslynWorkspaceService roslynService)
        : this(roslynService, codeFixProviders: null, codeRefactoringProviders: null)
    {
    }

    internal RoslynCodeActionService(
        RoslynWorkspaceService roslynService,
        IReadOnlyList<CodeFixProvider>? codeFixProviders,
        IReadOnlyList<CodeRefactoringProvider>? codeRefactoringProviders)
    {
        ArgumentNullException.ThrowIfNull(roslynService);
        _roslynService = roslynService;

        _codeFixProviders = codeFixProviders is null
            ? new Lazy<IReadOnlyList<CodeFixProvider>>(DiscoverCodeFixProviders)
            : new Lazy<IReadOnlyList<CodeFixProvider>>(() => codeFixProviders);
        _codeRefactoringProviders = codeRefactoringProviders is null
            ? new Lazy<IReadOnlyList<CodeRefactoringProvider>>(DiscoverCodeRefactoringProviders)
            : new Lazy<IReadOnlyList<CodeRefactoringProvider>>(() => codeRefactoringProviders);
    }

    /// <summary>
    /// Gets all available code actions (fixes + refactorings) at the specified position.
    /// </summary>
    public async Task<IReadOnlyList<CodeActionItem>> GetCodeActionsAsync(
        string filePath,
        int position,
        CancellationToken cancellationToken = default)
    {
        return await GetCodeActionsAsync(filePath, position, refactoringSelectionSpan: null, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<CodeActionItem>> GetCodeActionsAsync(
        string filePath,
        int position,
        TextSpan? refactoringSelectionSpan,
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
        await CollectRefactoringsAsync(
                document,
                position,
                refactoringSelectionSpan,
                results,
                providerFilter: null,
                cancellationToken)
            .ConfigureAwait(false);

        return results;
    }

    internal async Task<IReadOnlyList<CodeActionItem>> GetExtractMethodActionsAsync(
        string filePath,
        TextSpan selectionSpan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!RoslynWorkspaceService.IsCSharpFile(filePath) || selectionSpan.Length == 0)
        {
            return [];
        }

        Document? document = _roslynService.GetDocument(filePath);
        if (document is null)
        {
            return [];
        }

        List<CodeActionItem> results = [];
        await CollectRefactoringsAsync(
                document,
                selectionSpan.Start,
                selectionSpan,
                results,
                IsExtractMethodRefactoringProvider,
                cancellationToken)
            .ConfigureAwait(false);

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
                    RecordProviderSuccess(provider);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordProviderFailure(provider, "RegisterCodeFixes", $"DiagnosticId={diagnostic.Id}", ex);
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
                    RecordProviderSuccess(provider);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordProviderFailure(provider, "RegisterCodeFixes", $"DiagnosticId={diagnostic.Id}", ex);
                }
            }
        }
    }

    private async Task CollectRefactoringsAsync(
        Document document,
        int position,
        TextSpan? refactoringSelectionSpan,
        List<CodeActionItem> results,
        Func<CodeRefactoringProvider, bool>? providerFilter,
        CancellationToken cancellationToken)
    {
        SourceText sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        TextSpan span = CreateRefactoringSpan(sourceText, position, refactoringSelectionSpan);

        IReadOnlyList<CodeRefactoringProvider> providers = _codeRefactoringProviders.Value;
        HashSet<string> seenTitles = new(results.Select(r => r.Title), StringComparer.Ordinal);

        foreach (CodeRefactoringProvider provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (providerFilter is not null && !providerFilter(provider))
            {
                continue;
            }

            try
            {
                CodeRefactoringContext context = new(
                    document,
                    span,
                    action => AddLeafActions(action, results, seenTitles),
                    cancellationToken);

                await provider.ComputeRefactoringsAsync(context).ConfigureAwait(false);
                RecordProviderSuccess(provider);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordProviderFailure(provider, "ComputeRefactorings", $"Span={span.Start}..{span.End}", ex);
            }
        }
    }

    internal IReadOnlyList<CodeActionProviderTelemetrySnapshot> GetProviderTelemetrySnapshot()
    {
        return _providerInvocationCounters
            .Select(static pair => pair.Value.ToSnapshot(pair.Key))
            .OrderBy(static snapshot => snapshot.ProviderName, StringComparer.Ordinal)
            .ToList();
    }

    internal IReadOnlyList<CodeActionProviderFailureLogEntry> GetProviderFailureLogSnapshot()
    {
        return _providerFailureLogEntries.ToArray();
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

    private static TextSpan CreateRefactoringSpan(SourceText sourceText, int position, TextSpan? refactoringSelectionSpan)
    {
        int textLength = sourceText.Length;
        if (refactoringSelectionSpan is TextSpan selectionSpan)
        {
            int start = Math.Clamp(selectionSpan.Start, 0, textLength);
            int end = Math.Clamp(selectionSpan.End, 0, textLength);
            return TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
        }

        int clampedPosition = Math.Clamp(position, 0, textLength);
        return TextSpan.FromBounds(clampedPosition, clampedPosition);
    }

    private static bool IsExtractMethodRefactoringProvider(CodeRefactoringProvider provider)
    {
        string providerName = GetProviderName(provider);
        return providerName.Contains("ExtractMethod", StringComparison.Ordinal)
            || providerName.Contains("ExtractLocalFunction", StringComparison.Ordinal);
    }

    private static string GetProviderName(object provider)
    {
        return provider.GetType().FullName ?? provider.GetType().Name;
    }

    private void RecordProviderSuccess(object provider)
    {
        string providerName = GetProviderName(provider);
        ProviderInvocationCounters counters = _providerInvocationCounters.GetOrAdd(providerName, static _ => new ProviderInvocationCounters());
        counters.RecordSuccess();
    }

    private void RecordProviderFailure(object provider, string operation, string context, Exception exception)
    {
        string providerName = GetProviderName(provider);
        ProviderInvocationCounters counters = _providerInvocationCounters.GetOrAdd(providerName, static _ => new ProviderInvocationCounters());
        counters.RecordFailure();

        CodeActionProviderFailureLogEntry entry = new(
            DateTimeOffset.UtcNow,
            providerName,
            operation,
            context,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message);

        _providerFailureLogEntries.Enqueue(entry);
        while (_providerFailureLogEntries.Count > MaxProviderFailureLogEntries
            && _providerFailureLogEntries.TryDequeue(out _))
        {
        }

        Debug.WriteLine(
            $"[RoslynCodeActionService][Diagnostic] Provider='{providerName}' Operation='{operation}' Context='{context}' Exception='{entry.ExceptionType}' Message='{entry.Message}'");
    }

    private static IReadOnlyList<CodeFixProvider> DiscoverCodeFixProviders()
    {
        List<CodeFixProvider> providers = [];
        foreach (Assembly assembly in MefHostServices.DefaultAssemblies)
        {
            try
            {
                foreach (Type type in assembly.GetTypes())
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoslynCodeActionService][Diagnostic] Failed to discover code-fix providers from '{assembly.FullName}': {ex.Message}");
            }
        }

        Debug.WriteLine($"Discovered {providers.Count} C# CodeFixProviders");
        return providers;
    }

    private static IReadOnlyList<CodeRefactoringProvider> DiscoverCodeRefactoringProviders()
    {
        List<CodeRefactoringProvider> providers = [];
        foreach (Assembly assembly in MefHostServices.DefaultAssemblies)
        {
            try
            {
                foreach (Type type in assembly.GetTypes())
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[RoslynCodeActionService][Diagnostic] Failed to discover code-refactoring providers from '{assembly.FullName}': {ex.Message}");
            }
        }

        Debug.WriteLine($"Discovered {providers.Count} C# CodeRefactoringProviders");
        return providers;
    }

    /// <summary>
    /// Result of applying a code action.
    /// </summary>
    public sealed record ApplyResult(string NewText);

    private sealed class ProviderInvocationCounters
    {
        private int _successCount;
        private int _failureCount;

        public void RecordSuccess()
        {
            Interlocked.Increment(ref _successCount);
        }

        public void RecordFailure()
        {
            Interlocked.Increment(ref _failureCount);
        }

        public CodeActionProviderTelemetrySnapshot ToSnapshot(string providerName)
        {
            return new CodeActionProviderTelemetrySnapshot(
                providerName,
                Volatile.Read(ref _successCount),
                Volatile.Read(ref _failureCount));
        }
    }
}

internal sealed record CodeActionProviderTelemetrySnapshot(
    string ProviderName,
    int SuccessCount,
    int FailureCount)
{
    public int AttemptCount => SuccessCount + FailureCount;

    public double SuccessRate => AttemptCount == 0
        ? 0d
        : (double)SuccessCount / AttemptCount;

    public double FailureRate => AttemptCount == 0
        ? 0d
        : (double)FailureCount / AttemptCount;
}

internal sealed record CodeActionProviderFailureLogEntry(
    DateTimeOffset Timestamp,
    string ProviderName,
    string Operation,
    string Context,
    string ExceptionType,
    string Message);
