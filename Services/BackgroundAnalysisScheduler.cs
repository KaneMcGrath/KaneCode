using KaneCode.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;

namespace KaneCode.Services;

/// <summary>
/// Cached per-document analysis results so unchanged documents are not recomputed.
/// </summary>
internal sealed record AnalysisSnapshot(
    VersionStamp DocumentVersion,
    IReadOnlyList<ClassifiedSpan> ClassifiedSpans,
    IReadOnlyList<DiagnosticEntry> DiagnosticEntries,
    IReadOnlyList<DiagnosticItem> DiagnosticItems);

/// <summary>
/// Result payload delivered to the UI after a classification pass.
/// </summary>
internal sealed record ClassificationResult(
    string FilePath,
    IReadOnlyList<ClassifiedSpan> ClassifiedSpans);

/// <summary>
/// Result payload delivered to the UI after a diagnostics pass.
/// </summary>
internal sealed record DiagnosticsResult(
    IReadOnlyList<DiagnosticItem> AllItems,
    IReadOnlyList<DiagnosticEntry> ActiveFileEntries);

/// <summary>
/// Manages independent background tasks for classification, diagnostics, and navigation indexing.
/// <para>
/// Active-document work (classification, diagnostics for the edited file) uses short debounce
/// delays and is latency-sensitive. Background solution-wide work (diagnostics for all open
/// files) runs at lower priority with throttling to avoid UI stalls.
/// </para>
/// </summary>
internal sealed class BackgroundAnalysisScheduler : IDisposable
{
    private readonly RoslynWorkspaceService _roslynService;
    private readonly ConcurrentDictionary<string, AnalysisSnapshot> _snapshotCache = new(StringComparer.OrdinalIgnoreCase);

    // --- Independent cancellation per work type ---
    private CancellationTokenSource? _classificationCts;
    private CancellationTokenSource? _activeDocDiagnosticsCts;
    private CancellationTokenSource? _solutionDiagnosticsCts;

    // --- Configurable delays ---
    private readonly TimeSpan _classificationDelay;
    private readonly TimeSpan _activeDocDiagnosticsDelay;
    private readonly TimeSpan _solutionDiagnosticsDelay;

    // --- Throttle: minimum gap between expensive full-solution operations ---
    private readonly TimeSpan _solutionThrottleInterval;
    private long _lastSolutionRunTicks;

    // --- Incremental classification: maximum file length before switching to viewport-only classification ---
    private readonly int _incrementalClassificationThreshold;

    // --- Events to push results to the UI ---

    /// <summary>Raised on a background thread when classification results are ready.</summary>
    public event Action<ClassificationResult>? ClassificationCompleted;

    /// <summary>Raised on a background thread when diagnostics results are ready.</summary>
    public event Action<DiagnosticsResult>? DiagnosticsCompleted;

    private bool _disposed;

    /// <param name="roslynService">The Roslyn workspace service providing documents and diagnostics.</param>
    /// <param name="classificationDelay">Debounce delay before classification starts (default 200 ms).</param>
    /// <param name="activeDocDiagnosticsDelay">Debounce delay before active-document diagnostics start (default 300 ms).</param>
    /// <param name="solutionDiagnosticsDelay">Debounce delay before solution-wide diagnostics start (default 800 ms).</param>
    /// <param name="solutionThrottleInterval">Minimum gap between full-solution diagnostic runs (default 2 s).</param>
    /// <param name="incrementalClassificationThreshold">File length above which only visible-range classification is used (default 50 000 chars).</param>
    public BackgroundAnalysisScheduler(
        RoslynWorkspaceService roslynService,
        TimeSpan? classificationDelay = null,
        TimeSpan? activeDocDiagnosticsDelay = null,
        TimeSpan? solutionDiagnosticsDelay = null,
        TimeSpan? solutionThrottleInterval = null,
        int incrementalClassificationThreshold = 50_000)
    {
        ArgumentNullException.ThrowIfNull(roslynService);

        _roslynService = roslynService;
        _classificationDelay = classificationDelay ?? TimeSpan.FromMilliseconds(200);
        _activeDocDiagnosticsDelay = activeDocDiagnosticsDelay ?? TimeSpan.FromMilliseconds(300);
        _solutionDiagnosticsDelay = solutionDiagnosticsDelay ?? TimeSpan.FromMilliseconds(800);
        _solutionThrottleInterval = solutionThrottleInterval ?? TimeSpan.FromSeconds(2);
        _incrementalClassificationThreshold = incrementalClassificationThreshold;
    }

    /// <summary>
    /// Schedules classification for the active document. Cancels any previous in-flight classification.
    /// </summary>
    /// <param name="filePath">Path of the file to classify.</param>
    /// <param name="visibleStartOffset">Start offset of the visible range, or null for full file.</param>
    /// <param name="visibleEndOffset">End offset of the visible range, or null for full file.</param>
    public void ScheduleClassification(string filePath, int? visibleStartOffset = null, int? visibleEndOffset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!RoslynWorkspaceService.IsCSharpFile(filePath))
        {
            return;
        }

        _classificationCts?.Cancel();
        CancellationTokenSource cts = new();
        _classificationCts = cts;

        _ = RunClassificationAsync(filePath, visibleStartOffset, visibleEndOffset, cts.Token);
    }

    /// <summary>
    /// Schedules diagnostics for the active document and (separately, with lower priority) for all open documents.
    /// </summary>
    /// <param name="activeFilePath">The active file that was just edited.</param>
    /// <param name="openFilePaths">All open C# file paths for solution-wide analysis.</param>
    public void ScheduleDiagnostics(string activeFilePath, IReadOnlyList<string> openFilePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeFilePath);
        ArgumentNullException.ThrowIfNull(openFilePaths);

        // Active-document diagnostics: fast, latency-sensitive
        _activeDocDiagnosticsCts?.Cancel();
        CancellationTokenSource activeDocCts = new();
        _activeDocDiagnosticsCts = activeDocCts;

        // Solution-wide diagnostics: slower, throttled
        _solutionDiagnosticsCts?.Cancel();
        CancellationTokenSource solutionCts = new();
        _solutionDiagnosticsCts = solutionCts;

        _ = RunDiagnosticsAsync(activeFilePath, openFilePaths, activeDocCts.Token, solutionCts.Token);
    }

    /// <summary>
    /// Schedules all analysis work (classification + diagnostics) for an active-document edit.
    /// This is the main entry point that replaces the old <c>ScheduleRoslynAnalysis</c>.
    /// </summary>
    public void ScheduleFullAnalysis(string activeFilePath, string editorText, IReadOnlyList<string> openCSharpFilePaths,
        int? visibleStartOffset = null, int? visibleEndOffset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeFilePath);
        ArgumentNullException.ThrowIfNull(editorText);
        if (!RoslynWorkspaceService.IsCSharpFile(activeFilePath))
        {
            return;
        }

        // Update Roslyn text first, then schedule independent tasks
        _classificationCts?.Cancel();
        _activeDocDiagnosticsCts?.Cancel();
        _solutionDiagnosticsCts?.Cancel();

        CancellationTokenSource classCts = new();
        CancellationTokenSource activeDocCts = new();
        CancellationTokenSource solutionCts = new();

        _classificationCts = classCts;
        _activeDocDiagnosticsCts = activeDocCts;
        _solutionDiagnosticsCts = solutionCts;

        _ = RunFullAnalysisAsync(activeFilePath, editorText, openCSharpFilePaths,
            visibleStartOffset, visibleEndOffset,
            classCts.Token, activeDocCts.Token, solutionCts.Token);
    }

    /// <summary>
    /// Cancels all in-flight analysis work. Useful when loading a new project/solution.
    /// </summary>
    public void CancelAll()
    {
        _classificationCts?.Cancel();
        _activeDocDiagnosticsCts?.Cancel();
        _solutionDiagnosticsCts?.Cancel();
    }

    /// <summary>
    /// Invalidates cached snapshots for a document, forcing recomputation on the next analysis pass.
    /// </summary>
    public void InvalidateCache(string filePath)
    {
        _snapshotCache.TryRemove(filePath, out _);
    }

    /// <summary>
    /// Clears the entire snapshot cache.
    /// </summary>
    public void ClearCache()
    {
        _snapshotCache.Clear();
    }

    /// <summary>
    /// Returns the cached snapshot for a document if available, or null.
    /// </summary>
    internal AnalysisSnapshot? GetCachedSnapshot(string filePath)
    {
        _snapshotCache.TryGetValue(filePath, out AnalysisSnapshot? snapshot);
        return snapshot;
    }

    // -----------------------------------------------------------------------
    //  Classification
    // -----------------------------------------------------------------------

    private async Task RunClassificationAsync(string filePath, int? visibleStart, int? visibleEnd, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_classificationDelay, cancellationToken).ConfigureAwait(false);

            Document? document = _roslynService.GetDocument(filePath);
            if (document is null)
            {
                return;
            }

            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            TextSpan spanToClassify = DetermineClassificationSpan(text, visibleStart, visibleEnd);

            var spans = await Classifier.GetClassifiedSpansAsync(
                document, spanToClassify, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<ClassifiedSpan> result = spans.ToList();

            // Update cache
            AnalysisSnapshot? existing = GetCachedSnapshot(filePath);
            VersionStamp version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
            AnalysisSnapshot updated = new(
                version,
                result,
                existing?.DiagnosticEntries ?? [],
                existing?.DiagnosticItems ?? []);
            _snapshotCache[filePath] = updated;

            ClassificationCompleted?.Invoke(new ClassificationResult(filePath, result));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    private TextSpan DetermineClassificationSpan(SourceText text, int? visibleStart, int? visibleEnd)
    {
        // For large files, classify only the visible range (with padding) when viewport info is available
        if (text.Length > _incrementalClassificationThreshold && visibleStart.HasValue && visibleEnd.HasValue)
        {
            // Add generous padding so scrolling doesn't immediately show uncolored code
            int padding = Math.Min(5000, text.Length / 4);
            int start = Math.Max(0, visibleStart.Value - padding);
            int end = Math.Min(text.Length, visibleEnd.Value + padding);
            return TextSpan.FromBounds(start, end);
        }

        return TextSpan.FromBounds(0, text.Length);
    }

    // -----------------------------------------------------------------------
    //  Diagnostics
    // -----------------------------------------------------------------------

    private async Task RunDiagnosticsAsync(string activeFilePath, IReadOnlyList<string> openFilePaths,
        CancellationToken activeDocCt, CancellationToken solutionCt)
    {
        try
        {
            // Phase 1: active-document diagnostics (latency-sensitive, short delay)
            await Task.Delay(_activeDocDiagnosticsDelay, activeDocCt).ConfigureAwait(false);

            List<DiagnosticEntry> activeFileEntries = [];
            List<DiagnosticItem> activeFileItems = [];

            (activeFileEntries, activeFileItems) = await BuildDiagnosticsForFileAsync(activeFilePath, activeDocCt).ConfigureAwait(false);

            // Cache active file
            await CacheDiagnosticsAsync(activeFilePath, activeFileEntries, activeFileItems, activeDocCt).ConfigureAwait(false);

            // Deliver fast active-doc result immediately
            DiagnosticsCompleted?.Invoke(new DiagnosticsResult(activeFileItems, activeFileEntries));

            // Phase 2: solution-wide diagnostics (lower priority, throttled)
            if (!ShouldRunSolutionAnalysis())
            {
                return;
            }

            await Task.Delay(_solutionDiagnosticsDelay - _activeDocDiagnosticsDelay, solutionCt).ConfigureAwait(false);

            HashSet<string> filesToAnalyze = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in openFilePaths)
            {
                if (RoslynWorkspaceService.IsCSharpFile(path))
                {
                    filesToAnalyze.Add(path);
                }
            }

            // Include dependents of the active file
            foreach (string dep in _roslynService.GetDependentOpenDocumentFilePaths(activeFilePath))
            {
                if (RoslynWorkspaceService.IsCSharpFile(dep))
                {
                    filesToAnalyze.Add(dep);
                }
            }

            List<DiagnosticItem> allItems = new(activeFileItems);
            List<DiagnosticEntry> finalActiveEntries = activeFileEntries;

            foreach (string path in filesToAnalyze)
            {
                solutionCt.ThrowIfCancellationRequested();

                if (string.Equals(path, activeFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Already computed
                }

                // Check cache first
                AnalysisSnapshot? cached = GetCachedSnapshot(path);
                if (cached is not null)
                {
                    Document? doc = _roslynService.GetDocument(path);
                    if (doc is not null)
                    {
                        VersionStamp currentVersion = await doc.GetTextVersionAsync(solutionCt).ConfigureAwait(false);
                        if (currentVersion == cached.DocumentVersion && cached.DiagnosticItems.Count > 0)
                        {
                            allItems.AddRange(cached.DiagnosticItems);
                            continue;
                        }
                    }
                }

                (List<DiagnosticEntry> entries, List<DiagnosticItem> items) = await BuildDiagnosticsForFileAsync(path, solutionCt).ConfigureAwait(false);
                allItems.AddRange(items);

                await CacheDiagnosticsAsync(path, entries, items, solutionCt).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _lastSolutionRunTicks, Stopwatch.GetTimestamp());

            // Deliver full solution result
            DiagnosticsCompleted?.Invoke(new DiagnosticsResult(allItems, finalActiveEntries));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request
        }
    }

    private async Task RunFullAnalysisAsync(string activeFilePath, string editorText, IReadOnlyList<string> openCSharpFilePaths,
        int? visibleStart, int? visibleEnd,
        CancellationToken classCt, CancellationToken activeDocCt, CancellationToken solutionCt)
    {
        try
        {
            // Use the shorter classification delay as the initial debounce for the whole pass
            await Task.Delay(_classificationDelay, classCt).ConfigureAwait(false);

            // Push text into Roslyn
            await _roslynService.UpdateDocumentTextAsync(activeFilePath, editorText, classCt).ConfigureAwait(false);

            // Invalidate cache for the edited file
            InvalidateCache(activeFilePath);

            // Launch classification and diagnostics concurrently (they are independent)
            Task classificationTask = RunClassificationCoreAsync(activeFilePath, visibleStart, visibleEnd, classCt);
            Task diagnosticsTask = RunDiagnosticsCoreAsync(activeFilePath, openCSharpFilePaths, activeDocCt, solutionCt);

            await Task.WhenAll(classificationTask, diagnosticsTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
    }

    /// <summary>
    /// Core classification logic without debounce delay (used when delay was already applied).
    /// </summary>
    private async Task RunClassificationCoreAsync(string filePath, int? visibleStart, int? visibleEnd, CancellationToken cancellationToken)
    {
        try
        {
            Document? document = _roslynService.GetDocument(filePath);
            if (document is null)
            {
                return;
            }

            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            TextSpan spanToClassify = DetermineClassificationSpan(text, visibleStart, visibleEnd);

            var spans = await Classifier.GetClassifiedSpansAsync(
                document, spanToClassify, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<ClassifiedSpan> result = spans.ToList();

            AnalysisSnapshot? existing = GetCachedSnapshot(filePath);
            VersionStamp version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
            AnalysisSnapshot updated = new(
                version,
                result,
                existing?.DiagnosticEntries ?? [],
                existing?.DiagnosticItems ?? []);
            _snapshotCache[filePath] = updated;

            ClassificationCompleted?.Invoke(new ClassificationResult(filePath, result));
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
    }

    /// <summary>
    /// Core diagnostics logic without the initial debounce (used when delay was already applied).
    /// </summary>
    private async Task RunDiagnosticsCoreAsync(string activeFilePath, IReadOnlyList<string> openFilePaths,
        CancellationToken activeDocCt, CancellationToken solutionCt)
    {
        try
        {
            // Phase 1: active-document diagnostics
            // Small additional delay to let the semantic model settle after text update
            await Task.Delay(_activeDocDiagnosticsDelay - _classificationDelay, activeDocCt).ConfigureAwait(false);

            (List<DiagnosticEntry> activeFileEntries, List<DiagnosticItem> activeFileItems) =
                await BuildDiagnosticsForFileAsync(activeFilePath, activeDocCt).ConfigureAwait(false);

            await CacheDiagnosticsAsync(activeFilePath, activeFileEntries, activeFileItems, activeDocCt).ConfigureAwait(false);

            // Deliver active-doc result immediately
            DiagnosticsCompleted?.Invoke(new DiagnosticsResult(activeFileItems, activeFileEntries));

            // Phase 2: solution-wide
            if (!ShouldRunSolutionAnalysis())
            {
                return;
            }

            TimeSpan remainingSolutionDelay = _solutionDiagnosticsDelay - _activeDocDiagnosticsDelay;
            if (remainingSolutionDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingSolutionDelay, solutionCt).ConfigureAwait(false);
            }

            HashSet<string> filesToAnalyze = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in openFilePaths)
            {
                if (RoslynWorkspaceService.IsCSharpFile(path))
                {
                    filesToAnalyze.Add(path);
                }
            }

            foreach (string dep in _roslynService.GetDependentOpenDocumentFilePaths(activeFilePath))
            {
                if (RoslynWorkspaceService.IsCSharpFile(dep))
                {
                    filesToAnalyze.Add(dep);
                }
            }

            List<DiagnosticItem> allItems = new(activeFileItems);

            foreach (string path in filesToAnalyze)
            {
                solutionCt.ThrowIfCancellationRequested();

                if (string.Equals(path, activeFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AnalysisSnapshot? cached = GetCachedSnapshot(path);
                if (cached is not null)
                {
                    Document? doc = _roslynService.GetDocument(path);
                    if (doc is not null)
                    {
                        VersionStamp currentVersion = await doc.GetTextVersionAsync(solutionCt).ConfigureAwait(false);
                        if (currentVersion == cached.DocumentVersion && cached.DiagnosticItems.Count > 0)
                        {
                            allItems.AddRange(cached.DiagnosticItems);
                            continue;
                        }
                    }
                }

                (List<DiagnosticEntry> entries, List<DiagnosticItem> items) =
                    await BuildDiagnosticsForFileAsync(path, solutionCt).ConfigureAwait(false);
                allItems.AddRange(items);

                await CacheDiagnosticsAsync(path, entries, items, solutionCt).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _lastSolutionRunTicks, Stopwatch.GetTimestamp());

            DiagnosticsCompleted?.Invoke(new DiagnosticsResult(allItems, activeFileEntries));
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private bool ShouldRunSolutionAnalysis()
    {
        long lastTicks = Interlocked.Read(ref _lastSolutionRunTicks);
        if (lastTicks == 0)
        {
            return true;
        }

        long elapsed = Stopwatch.GetTimestamp() - lastTicks;
        double elapsedMs = (double)elapsed / Stopwatch.Frequency * 1000;
        return elapsedMs >= _solutionThrottleInterval.TotalMilliseconds;
    }

    private async Task<(List<DiagnosticEntry> Entries, List<DiagnosticItem> Items)> BuildDiagnosticsForFileAsync(
        string filePath, CancellationToken cancellationToken)
    {
        IReadOnlyList<Diagnostic> diagnostics = await _roslynService.GetDiagnosticsAsync(filePath, cancellationToken).ConfigureAwait(false);
        List<DiagnosticEntry> entries = [];
        List<(DiagnosticEntry Entry, string Category)> diagDetails = [];

        foreach (Diagnostic diag in diagnostics)
        {
            if (diag.Severity == DiagnosticSeverity.Hidden)
            {
                continue;
            }

            TextSpan span = diag.Location.SourceSpan;
            DiagnosticEntry entry = new(span.Start, span.End, diag.Severity, diag.GetMessage(), diag.Id);
            entries.Add(entry);
            diagDetails.Add((entry, diag.Descriptor.Category));
        }

        Document? document = _roslynService.GetDocument(filePath);
        SourceText? sourceText = document is not null
            ? await document.GetTextAsync(cancellationToken).ConfigureAwait(false)
            : null;

        string projectName = document?.Project.Name ?? "";
        string fileName = System.IO.Path.GetFileName(filePath);

        List<DiagnosticItem> items = [];
        foreach ((DiagnosticEntry entry, string category) in diagDetails)
        {
            int line = 0;
            int column = 0;
            if (sourceText is not null && entry.Start >= 0 && entry.Start <= sourceText.Length)
            {
                Microsoft.CodeAnalysis.Text.LinePosition linePosition = sourceText.Lines.GetLinePosition(entry.Start);
                line = linePosition.Line + 1;
                column = linePosition.Character + 1;
            }

            items.Add(new DiagnosticItem(
                entry.Severity, entry.Id, entry.Message,
                fileName, line, column,
                entry.Start, entry.End, filePath,
                category, projectName));
        }

        return (entries, items);
    }

    private async Task CacheDiagnosticsAsync(string filePath, List<DiagnosticEntry> entries, List<DiagnosticItem> items,
        CancellationToken cancellationToken)
    {
        Document? doc = _roslynService.GetDocument(filePath);
        if (doc is null)
        {
            return;
        }

        VersionStamp version = await doc.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
        AnalysisSnapshot? existing = GetCachedSnapshot(filePath);
        AnalysisSnapshot updated = new(
            version,
            existing?.ClassifiedSpans ?? [],
            entries,
            items);
        _snapshotCache[filePath] = updated;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _classificationCts?.Cancel();
        _classificationCts?.Dispose();
        _activeDocDiagnosticsCts?.Cancel();
        _activeDocDiagnosticsCts?.Dispose();
        _solutionDiagnosticsCts?.Cancel();
        _solutionDiagnosticsCts?.Dispose();
    }
}
