using KaneCode.Models;
using KaneCode.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using System.IO;

namespace KaneCode.Tests.Services;

public sealed class RoslynCodeActionServiceTests : IDisposable
{
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly string _tempDirectory;

    public RoslynCodeActionServiceTests()
    {
        _workspaceService = new RoslynWorkspaceService();
        _tempDirectory = Path.Combine(Path.GetTempPath(), "KaneCodeCodeActions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        _workspaceService.Dispose();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task WhenSelectionSpanIsProvidedThenRefactoringProviderReceivesSelection()
    {
        string filePath = Path.Combine(_tempDirectory, "Selection.cs");
        string code = "class C { void M() { int value = 1; value++; } }";
        await _workspaceService.OpenOrUpdateDocumentAsync(filePath, code);

        TrackingRefactoringProvider provider = new();
        RoslynCodeActionService service = new(
            _workspaceService,
            codeFixProviders: [],
            codeRefactoringProviders: [provider]);

        int selectionStart = code.IndexOf("int value", StringComparison.Ordinal);
        TextSpan selectionSpan = new(selectionStart, "int value = 1;".Length);

        IReadOnlyList<CodeActionItem> actions = await service.GetCodeActionsAsync(
            filePath,
            selectionSpan.Start,
            selectionSpan,
            CancellationToken.None);

        Assert.Equal(selectionSpan, provider.LastSpan);
        Assert.Single(actions);
    }

    [Fact]
    public async Task WhenExtractMethodActionsRequestedThenOnlyExtractProvidersAreUsed()
    {
        string filePath = Path.Combine(_tempDirectory, "Extract.cs");
        string code = "class C { void M() { int value = 1; value++; } }";
        await _workspaceService.OpenOrUpdateDocumentAsync(filePath, code);

        ExtractMethodCodeRefactoringProvider extractProvider = new();
        NonExtractRefactoringProvider otherProvider = new();
        RoslynCodeActionService service = new(
            _workspaceService,
            codeFixProviders: [],
            codeRefactoringProviders: [extractProvider, otherProvider]);

        int selectionStart = code.IndexOf("int value", StringComparison.Ordinal);
        TextSpan selectionSpan = new(selectionStart, "int value = 1;".Length);

        IReadOnlyList<CodeActionItem> actions = await service.GetExtractMethodActionsAsync(
            filePath,
            selectionSpan,
            CancellationToken.None);

        Assert.Single(actions);
        Assert.Equal("Create helper", actions[0].Title);
        Assert.Equal(1, extractProvider.InvocationCount);
        Assert.Equal(0, otherProvider.InvocationCount);
    }

    [Fact]
    public async Task WhenProvidersRunThenTelemetryTracksSuccessAndFailureCounts()
    {
        string filePath = Path.Combine(_tempDirectory, "Telemetry.cs");
        string code = "class C { void M() { } }";
        await _workspaceService.OpenOrUpdateDocumentAsync(filePath, code);

        TrackingRefactoringProvider successProvider = new();
        ThrowingRefactoringProvider failureProvider = new();
        RoslynCodeActionService service = new(
            _workspaceService,
            codeFixProviders: [],
            codeRefactoringProviders: [successProvider, failureProvider]);

        IReadOnlyList<CodeActionItem> actions = await service.GetCodeActionsAsync(
            filePath,
            position: 0,
            refactoringSelectionSpan: null,
            CancellationToken.None);

        IReadOnlyList<CodeActionProviderTelemetrySnapshot> telemetry = service.GetProviderTelemetrySnapshot();
        CodeActionProviderTelemetrySnapshot successTelemetry = Assert.Single(
            telemetry.Where(static entry => entry.ProviderName.Contains(nameof(TrackingRefactoringProvider), StringComparison.Ordinal)));
        CodeActionProviderTelemetrySnapshot failureTelemetry = Assert.Single(
            telemetry.Where(static entry => entry.ProviderName.Contains(nameof(ThrowingRefactoringProvider), StringComparison.Ordinal)));

        Assert.Single(actions);
        Assert.Equal(1, successTelemetry.SuccessCount);
        Assert.Equal(0, successTelemetry.FailureCount);
        Assert.Equal(0, failureTelemetry.SuccessCount);
        Assert.Equal(1, failureTelemetry.FailureCount);
    }

    [Fact]
    public async Task WhenProviderThrowsThenFailureIsLogged()
    {
        string filePath = Path.Combine(_tempDirectory, "Failure.cs");
        string code = "class C { void M() { } }";
        await _workspaceService.OpenOrUpdateDocumentAsync(filePath, code);

        ThrowingRefactoringProvider failureProvider = new();
        RoslynCodeActionService service = new(
            _workspaceService,
            codeFixProviders: [],
            codeRefactoringProviders: [failureProvider]);

        IReadOnlyList<CodeActionItem> actions = await service.GetCodeActionsAsync(
            filePath,
            position: 0,
            refactoringSelectionSpan: null,
            CancellationToken.None);

        CodeActionProviderFailureLogEntry failure = Assert.Single(service.GetProviderFailureLogSnapshot());

        Assert.Empty(actions);
        Assert.Contains(nameof(ThrowingRefactoringProvider), failure.ProviderName, StringComparison.Ordinal);
        Assert.Equal("ComputeRefactorings", failure.Operation);
        Assert.Contains("Span=0..0", failure.Context, StringComparison.Ordinal);
        Assert.Contains("Intentional provider failure", failure.Message, StringComparison.Ordinal);
    }

    private sealed class TrackingRefactoringProvider : CodeRefactoringProvider
    {
        public TextSpan? LastSpan { get; private set; }

        public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            LastSpan = context.Span;
            context.RegisterRefactoring(CodeAction.Create(
                "Selection-aware action",
                cancellationToken => Task.FromResult(context.Document.Project.Solution)));
            return Task.CompletedTask;
        }
    }

    private sealed class ExtractMethodCodeRefactoringProvider : CodeRefactoringProvider
    {
        public int InvocationCount { get; private set; }

        public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            InvocationCount++;
            context.RegisterRefactoring(CodeAction.Create(
                "Create helper",
                cancellationToken => Task.FromResult(context.Document.Project.Solution)));
            return Task.CompletedTask;
        }
    }

    private sealed class NonExtractRefactoringProvider : CodeRefactoringProvider
    {
        public int InvocationCount { get; private set; }

        public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            InvocationCount++;
            context.RegisterRefactoring(CodeAction.Create(
                "Other action",
                cancellationToken => Task.FromResult(context.Document.Project.Solution)));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRefactoringProvider : CodeRefactoringProvider
    {
        public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            throw new InvalidOperationException("Intentional provider failure");
        }
    }
}
