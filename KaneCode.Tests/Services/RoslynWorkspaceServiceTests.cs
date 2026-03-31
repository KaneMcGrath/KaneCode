using KaneCode.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using Xunit;

namespace KaneCode.Tests.Services;

public class RoslynWorkspaceServiceTests : IDisposable
{
    private readonly RoslynWorkspaceService _service;

    public RoslynWorkspaceServiceTests()
    {
        _service = new RoslynWorkspaceService();
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    // --- GetDiagnosticsAsync: compiler diagnostics ---

    [Fact]
    public async Task WhenDocumentHasCompilerErrorsThenGetDiagnosticsReturnsThem()
    {
        string filePath = @"C:\TestProject\Broken.cs";
        string code = "class Foo { int x = unknownSymbol; }";

        await _service.OpenOrUpdateDocumentAsync(filePath, code);

        IReadOnlyList<Diagnostic> diagnostics = await _service.GetDiagnosticsAsync(filePath);

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Id == "CS0103"); // name does not exist
    }

    [Fact]
    public async Task WhenDocumentHasNoErrorsThenGetDiagnosticsReturnsEmpty()
    {
        string filePath = @"C:\TestProject\Clean.cs";
        string code = "namespace Test { public class Foo { } }";

        await _service.OpenOrUpdateDocumentAsync(filePath, code);

        IReadOnlyList<Diagnostic> diagnostics = await _service.GetDiagnosticsAsync(filePath);

        // Filter to errors only; there may be hidden diagnostics
        IEnumerable<Diagnostic> errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task WhenDocumentDoesNotExistThenGetDiagnosticsReturnsEmpty()
    {
        IReadOnlyList<Diagnostic> diagnostics = await _service.GetDiagnosticsAsync(@"C:\NoSuchFile.cs");

        Assert.Empty(diagnostics);
    }

    // --- GetDiagnosticsAsync: analyzer diagnostics ---

    [Fact]
    public async Task WhenAnalyzerRegisteredThenAnalyzerDiagnosticsIncludedInResults()
    {
        TestAnalyzer analyzer = new();
        AnalyzerImageReference analyzerRef = new(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

        CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp12);
        List<MetadataReference> references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        ProjectId projectId = await _service.AddProjectAsync(
            "AnalyzerTestProject",
            compilationOptions,
            parseOptions,
            references,
            [analyzerRef]);

        string filePath = @"C:\AnalyzerTest\Sample.cs";
        string code = "namespace Test { public class MyClass { } }";
        await _service.AddDocumentToProjectAsync(projectId, filePath, code);

        IReadOnlyList<Diagnostic> diagnostics = await _service.GetDiagnosticsAsync(filePath);

        Assert.Contains(diagnostics, d => d.Id == TestAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task WhenNoAnalyzersRegisteredThenOnlyCompilerDiagnosticsReturned()
    {
        CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp12);
        List<MetadataReference> references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        ProjectId projectId = await _service.AddProjectAsync(
            "NoAnalyzerProject",
            compilationOptions,
            parseOptions,
            references);

        string filePath = @"C:\NoAnalyzerTest\Sample.cs";
        string code = "class Foo { int x = unknownSymbol; }";
        await _service.AddDocumentToProjectAsync(projectId, filePath, code);

        IReadOnlyList<Diagnostic> diagnostics = await _service.GetDiagnosticsAsync(filePath);

        // Should have compiler error but no analyzer diagnostics
        Assert.Contains(diagnostics, d => d.Id == "CS0103");
        Assert.DoesNotContain(diagnostics, d => d.Id == TestAnalyzer.DiagnosticId);
    }

    /// <summary>
    /// Minimal analyzer that reports a warning on every class declaration.
    /// Used to verify that analyzer diagnostics flow through <see cref="RoslynWorkspaceService.GetDiagnosticsAsync"/>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class TestAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "TEST001";

        private static readonly DiagnosticDescriptor s_rule = new(
            DiagnosticId,
            "Test Analyzer",
            "Test diagnostic on class '{0}'",
            "Test",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
        }

        private static void AnalyzeSymbol(SymbolAnalysisContext context)
        {
            INamedTypeSymbol namedType = (INamedTypeSymbol)context.Symbol;
            context.ReportDiagnostic(Diagnostic.Create(s_rule, namedType.Locations[0], namedType.Name));
        }
    }
}
