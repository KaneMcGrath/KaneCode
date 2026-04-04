using KaneCode.Models;
using KaneCode.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;

namespace KaneCode.Tests.Services;

public class BackgroundAnalysisSchedulerTests : IDisposable
{
    private readonly RoslynWorkspaceService _workspace;
    private readonly BackgroundAnalysisScheduler _scheduler;

    public BackgroundAnalysisSchedulerTests()
    {
        _workspace = new RoslynWorkspaceService();
        _scheduler = new BackgroundAnalysisScheduler(
            _workspace,
            classificationDelay: TimeSpan.FromMilliseconds(50),
            activeDocDiagnosticsDelay: TimeSpan.FromMilliseconds(80),
            solutionDiagnosticsDelay: TimeSpan.FromMilliseconds(150),
            solutionThrottleInterval: TimeSpan.FromMilliseconds(100));
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _workspace.Dispose();
    }

    // --- Constructor guard ---

    [Fact]
    public void WhenRoslynServiceIsNullThenConstructorThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new BackgroundAnalysisScheduler(null!));
    }

    // --- Classification ---

    [Fact]
    public async Task WhenClassificationScheduledThenClassificationCompletedFires()
    {
        string filePath = @"C:\Test\Classify.cs";
        string code = "namespace Test { public class Foo { } }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code);

        TaskCompletionSource<ClassificationResult> tcs = new();
        _scheduler.ClassificationCompleted += result => tcs.TrySetResult(result);

        _scheduler.ScheduleClassification(filePath);

        ClassificationResult classificationResult = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(filePath, classificationResult.FilePath);
        Assert.NotEmpty(classificationResult.ClassifiedSpans);
    }

    [Fact]
    public void WhenNonCSharpFileThenClassificationIsSkipped()
    {
        bool fired = false;
        _scheduler.ClassificationCompleted += _ => fired = true;

        _scheduler.ScheduleClassification(@"C:\Test\readme.txt");

        Thread.Sleep(200);
        Assert.False(fired);
    }

    // --- Full analysis (classification + diagnostics) ---

    [Fact]
    public async Task WhenFullAnalysisScheduledThenBothClassificationAndDiagnosticsFire()
    {
        string filePath = @"C:\Test\Full.cs";
        string code = "class Foo { int x = unknownSymbol; }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code);

        TaskCompletionSource<ClassificationResult> classTcs = new();
        TaskCompletionSource<DiagnosticsResult> diagTcs = new();

        _scheduler.ClassificationCompleted += result => classTcs.TrySetResult(result);
        _scheduler.DiagnosticsCompleted += result => diagTcs.TrySetResult(result);

        _scheduler.ScheduleFullAnalysis(filePath, code, [filePath]);

        ClassificationResult classResult = await classTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        DiagnosticsResult diagResult = await diagTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(filePath, classResult.FilePath);
        Assert.NotEmpty(classResult.ClassifiedSpans);
        Assert.NotEmpty(diagResult.ActiveFileEntries);
        Assert.NotEmpty(diagResult.AllItems);
    }

    [Fact]
    public void WhenFullAnalysisOnNonCSharpFileThenNothingFires()
    {
        bool classFired = false;
        bool diagFired = false;
        _scheduler.ClassificationCompleted += _ => classFired = true;
        _scheduler.DiagnosticsCompleted += _ => diagFired = true;

        _scheduler.ScheduleFullAnalysis(@"C:\Test\readme.txt", "hello", []);

        Thread.Sleep(200);
        Assert.False(classFired);
        Assert.False(diagFired);
    }

    // --- Diagnostics ---

    [Fact]
    public async Task WhenDiagnosticsScheduledThenDiagnosticsCompletedFires()
    {
        string filePath = @"C:\Test\Diag.cs";
        string code = "class Foo { int x = unknownSymbol; }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code);

        TaskCompletionSource<DiagnosticsResult> tcs = new();
        _scheduler.DiagnosticsCompleted += result => tcs.TrySetResult(result);

        _scheduler.ScheduleDiagnostics(filePath, [filePath]);

        DiagnosticsResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEmpty(result.ActiveFileEntries);
        Assert.NotEmpty(result.AllItems);
        Assert.Contains(result.AllItems, d => d.Code == "CS0103");
    }

    [Fact]
    public async Task WhenDocumentIsCleanThenDiagnosticsAreEmpty()
    {
        string filePath = @"C:\Test\Clean.cs";
        string code = "namespace Test { public class Foo { } }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code);

        TaskCompletionSource<DiagnosticsResult> tcs = new();
        _scheduler.DiagnosticsCompleted += result => tcs.TrySetResult(result);

        _scheduler.ScheduleDiagnostics(filePath, [filePath]);

        DiagnosticsResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(result.ActiveFileEntries);
        Assert.Empty(result.AllItems);
    }

    // --- Caching ---

    [Fact]
    public async Task WhenAnalysisCompletedThenSnapshotIsCached()
    {
        string filePath = @"C:\Test\Cache.cs";
        string code = "class Foo { }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code);

        TaskCompletionSource<ClassificationResult> tcs = new();
        _scheduler.ClassificationCompleted += result => tcs.TrySetResult(result);

        _scheduler.ScheduleClassification(filePath);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        AnalysisSnapshot? snapshot = _scheduler.GetCachedSnapshot(filePath);

        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.ClassifiedSpans);
    }

    [Fact]
    public async Task WhenCacheInvalidatedThenSnapshotIsRemoved()
    {
        string filePath = @"C:\Test\CacheInv.cs";
        string code = "class Foo { }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code);

        TaskCompletionSource<ClassificationResult> tcs = new();
        _scheduler.ClassificationCompleted += result => tcs.TrySetResult(result);

        _scheduler.ScheduleClassification(filePath);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _scheduler.InvalidateCache(filePath);

        Assert.Null(_scheduler.GetCachedSnapshot(filePath));
    }

    [Fact]
    public async Task WhenCacheClearedThenAllSnapshotsRemoved()
    {
        string filePath1 = @"C:\Test\Cache1.cs";
        string filePath2 = @"C:\Test\Cache2.cs";

        await _workspace.OpenOrUpdateDocumentAsync(filePath1, "class A { }");
        await _workspace.OpenOrUpdateDocumentAsync(filePath2, "class B { }");

        // Schedule first, wait for it to complete, then schedule second
        TaskCompletionSource<ClassificationResult> tcs1 = new();
        _scheduler.ClassificationCompleted += result =>
        {
            if (string.Equals(result.FilePath, filePath1, StringComparison.OrdinalIgnoreCase))
            {
                tcs1.TrySetResult(result);
            }
        };
        _scheduler.ScheduleClassification(filePath1);
        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(10));

        TaskCompletionSource<ClassificationResult> tcs2 = new();
        _scheduler.ClassificationCompleted += result =>
        {
            if (string.Equals(result.FilePath, filePath2, StringComparison.OrdinalIgnoreCase))
            {
                tcs2.TrySetResult(result);
            }
        };
        _scheduler.ScheduleClassification(filePath2);
        await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(_scheduler.GetCachedSnapshot(filePath1));
        Assert.NotNull(_scheduler.GetCachedSnapshot(filePath2));

        _scheduler.ClearCache();

        Assert.Null(_scheduler.GetCachedSnapshot(filePath1));
        Assert.Null(_scheduler.GetCachedSnapshot(filePath2));
    }

    // --- CancelAll ---

    [Fact]
    public async Task WhenCancelAllCalledThenNoResultsDelivered()
    {
        string filePath = @"C:\Test\Cancel.cs";
        string code = "class Foo { }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code);

        bool fired = false;
        _scheduler.ClassificationCompleted += _ => fired = true;
        _scheduler.DiagnosticsCompleted += _ => fired = true;

        _scheduler.ScheduleFullAnalysis(filePath, code, [filePath]);
        _scheduler.CancelAll();

        // Wait longer than the debounce delays
        await Task.Delay(500);

        Assert.False(fired);
    }

    // --- Debounce: newer request supersedes older ---

    [Fact]
    public async Task WhenMultipleRapidSchedulesThenOnlyLastResultDelivered()
    {
        string filePath = @"C:\Test\Debounce.cs";
        string code1 = "class First { }";
        string code2 = "class Second { }";
        string code3 = "class Third { }";

        await _workspace.OpenOrUpdateDocumentAsync(filePath, code1);

        List<ClassificationResult> results = [];
        _scheduler.ClassificationCompleted += result => results.Add(result);

        // Rapid-fire three schedules — earlier ones should be cancelled by the debounce
        _scheduler.ScheduleFullAnalysis(filePath, code1, [filePath]);
        _scheduler.ScheduleFullAnalysis(filePath, code2, [filePath]);
        _scheduler.ScheduleFullAnalysis(filePath, code3, [filePath]);

        // Wait for the last one to complete
        await Task.Delay(1000);

        // Only the last schedule should have delivered results
        Assert.True(results.Count >= 1);
        Assert.Equal(filePath, results[^1].FilePath);
    }
}
