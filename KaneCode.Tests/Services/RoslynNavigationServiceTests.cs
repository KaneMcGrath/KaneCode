using KaneCode.Models;
using KaneCode.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.IO;
using Xunit;

namespace KaneCode.Tests.Services;

public sealed class RoslynNavigationServiceTests : IDisposable
{
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly RoslynNavigationService _navigationService;

    public RoslynNavigationServiceTests()
    {
        _workspaceService = new RoslynWorkspaceService();
        _navigationService = new RoslynNavigationService(_workspaceService);
    }

    public void Dispose()
    {
        _workspaceService.Dispose();
    }

    [Fact]
    public async Task WhenFindingReferencesThenResultsIncludeProjectAndKinds()
    {
        ProjectId projectId = await CreateProjectAsync("NavigationProject");

        string declarationFilePath = @"C:\NavigationProject\Sample.cs";
        string declarationCode = "namespace Demo; public sealed class Sample { public void Method() { } }";
        await _workspaceService.AddDocumentToProjectAsync(projectId, declarationFilePath, declarationCode);

        string usageFilePath = @"C:\NavigationProject\Consumer.cs";
        string usageCode = "namespace Demo; public sealed class Consumer { public void Call(Sample sample) { sample.Method(); } }";
        await _workspaceService.AddDocumentToProjectAsync(projectId, usageFilePath, usageCode);

        int position = declarationCode.IndexOf("Method", StringComparison.Ordinal);

        IReadOnlyList<ReferenceItem> results = await _navigationService.FindReferencesAsync(declarationFilePath, position);

        Assert.Contains(results, item => item.Kind == ReferenceKind.Definition);
        Assert.Contains(results, item => item.Kind == ReferenceKind.Reference);
        Assert.All(results, item => Assert.Equal("NavigationProject", item.ProjectName));
    }

    [Fact]
    public async Task WhenFindingImplementationsThenResultsAreMarkedAsImplementations()
    {
        ProjectId projectId = await CreateProjectAsync("ImplementationsProject");

        string interfaceFilePath = @"C:\ImplementationsProject\IGreeter.cs";
        string interfaceCode = "namespace Demo; public interface IGreeter { void Greet(); }";
        await _workspaceService.AddDocumentToProjectAsync(projectId, interfaceFilePath, interfaceCode);

        string implementationFilePath = @"C:\ImplementationsProject\Greeter.cs";
        string implementationCode = "namespace Demo; public sealed class Greeter : IGreeter { public void Greet() { } }";
        await _workspaceService.AddDocumentToProjectAsync(projectId, implementationFilePath, implementationCode);

        int position = interfaceCode.IndexOf("IGreeter", StringComparison.Ordinal);

        IReadOnlyList<ReferenceItem> results = await _navigationService.FindImplementationsAsync(interfaceFilePath, position);

        Assert.Contains(results, item => item.Kind == ReferenceKind.Implementation);
        Assert.Contains(results, item => item.FilePath == implementationFilePath);
    }

    [Fact]
    public async Task WhenSearchingSymbolsThenMatchingDeclarationsAreReturned()
    {
        ProjectId projectId = await CreateProjectAsync("SearchProject");

        string filePath = @"C:\SearchProject\Symbols.cs";
        string code = "namespace Demo; public sealed class AlphaService { } public sealed class AlphaHelper { } public sealed class BetaService { }";
        await _workspaceService.AddDocumentToProjectAsync(projectId, filePath, code);

        IReadOnlyList<ReferenceItem> results = await _navigationService.SearchSymbolsAsync("Alpha");

        Assert.Contains(results, item => item.SymbolName.Contains("AlphaService", StringComparison.Ordinal));
        Assert.Contains(results, item => item.SymbolName.Contains("AlphaHelper", StringComparison.Ordinal));
        Assert.All(results, item => Assert.Equal(ReferenceKind.Definition, item.Kind));
    }

    private async Task<ProjectId> CreateProjectAsync(string projectName)
    {
        CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp12);
        List<MetadataReference> references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        ];

        return await _workspaceService.AddProjectAsync(projectName, compilationOptions, parseOptions, references);
    }
}
