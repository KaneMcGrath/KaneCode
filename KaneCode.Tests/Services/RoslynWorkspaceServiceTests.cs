using KaneCode.Services;
using KaneCode.ViewModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.IO;
using Xunit;

namespace KaneCode.Tests.Services;

public class RoslynWorkspaceServiceTests : IDisposable
{
    private readonly RoslynWorkspaceService _service;
    private readonly string _tempDir;

    public RoslynWorkspaceServiceTests()
    {
        _service = new RoslynWorkspaceService();
        _tempDir = Path.Combine(Path.GetTempPath(), "KaneCodeWsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
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

    // --- EditorConfig severity overrides ---

    [Fact]
    public async Task WhenEditorConfigElevatesSeverityThenDiagnosticReflectsIt()
    {
        // TestAnalyzer reports TEST001 as Warning by default.
        // An .editorconfig with dotnet_diagnostic.TEST001.severity = error should elevate it.
        string editorConfigPath = Path.Combine(_tempDir, ".editorconfig");
        File.WriteAllText(editorConfigPath, """
            root = true
            [*.cs]
            dotnet_diagnostic.TEST001.severity = error
            """);

        TestAnalyzer analyzer = new();
        AnalyzerImageReference analyzerRef = new(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp12);
        List<MetadataReference> references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        ProjectId projectId = await _service.AddProjectAsync(
            "EditorConfigSeverityTest", compilationOptions, parseOptions, references, [analyzerRef]);

        // Source file path under the temp dir so the editorconfig scope matches
        string filePath = Path.Combine(_tempDir, "Sample.cs");
        await _service.AddDocumentToProjectAsync(projectId, filePath,
            "namespace Test { public class MyClass { } }");

        await _service.AddAnalyzerConfigDocumentsToProjectAsync(projectId, [editorConfigPath]);

        IReadOnlyList<Diagnostic> diagnostics = await _service.GetDiagnosticsAsync(filePath);

        Assert.Contains(diagnostics, d => d.Id == TestAnalyzer.DiagnosticId && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task WhenEditorConfigSuppressesDiagnosticThenItIsNotReportedAsWarning()
    {
        // An .editorconfig with dotnet_diagnostic.TEST001.severity = none should suppress the diagnostic.
        string editorConfigPath = Path.Combine(_tempDir, ".editorconfig");
        File.WriteAllText(editorConfigPath, """
            root = true
            [*.cs]
            dotnet_diagnostic.TEST001.severity = none
            """);

        TestAnalyzer analyzer = new();
        AnalyzerImageReference analyzerRef = new(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp12);
        List<MetadataReference> references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        ProjectId projectId = await _service.AddProjectAsync(
            "EditorConfigSuppressTest", compilationOptions, parseOptions, references, [analyzerRef]);

        string filePath = Path.Combine(_tempDir, "Suppressed.cs");
        await _service.AddDocumentToProjectAsync(projectId, filePath,
            "namespace Test { public class MyClass { } }");

        await _service.AddAnalyzerConfigDocumentsToProjectAsync(projectId, [editorConfigPath]);

        IReadOnlyList<Diagnostic> diagnostics = await _service.GetDiagnosticsAsync(filePath);

        // TEST001 should be suppressed (either absent or hidden severity)
        Assert.DoesNotContain(diagnostics,
            d => d.Id == TestAnalyzer.DiagnosticId && d.Severity == DiagnosticSeverity.Warning);
    }

    // --- Additional documents ---

    [Fact]
    public async Task WhenAdditionalDocumentsAddedThenProjectContainsThem()
    {
        CSharpCompilationOptions compilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp12);
        List<MetadataReference> references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        ProjectId projectId = await _service.AddProjectAsync(
            "AdditionalDocsTest", compilationOptions, parseOptions, references);

        string additionalFilePath = Path.Combine(_tempDir, "PublicAPI.Shipped.txt");
        File.WriteAllText(additionalFilePath, "T:MyNamespace.MyClass");

        await _service.AddAdditionalDocumentsToProjectAsync(projectId, [additionalFilePath]);

        Project? project = _service.Workspace.CurrentSolution.GetProject(projectId);
        Assert.NotNull(project);
        Assert.Contains(project.AdditionalDocuments,
            d => d.FilePath != null && d.FilePath.Equals(additionalFilePath, StringComparison.OrdinalIgnoreCase));
    }

    // --- GetAllTrackedDocumentFilePaths ---

    [Fact]
    public async Task WhenDocumentsAddedThenGetAllTrackedDocumentFilePathsReturnsThem()
    {
        string filePath1 = @"C:\Tracked\File1.cs";
        string filePath2 = @"C:\Tracked\File2.cs";
        await _service.OpenOrUpdateDocumentAsync(filePath1, "class A { }");
        await _service.OpenOrUpdateDocumentAsync(filePath2, "class B { }");

        IReadOnlyList<string> tracked = _service.GetAllTrackedDocumentFilePaths();

        Assert.Contains(tracked, p => p.Equals(filePath1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tracked, p => p.Equals(filePath2, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenNoDocumentsTrackedThenGetAllTrackedDocumentFilePathsReturnsEmpty()
    {
        // The default project has no documents initially
        IReadOnlyList<string> tracked = _service.GetAllTrackedDocumentFilePaths();

        Assert.Empty(tracked);
    }

    [Fact]
    public async Task WhenDocumentRemovedThenItIsNotInTrackedPaths()
    {
        string filePath = @"C:\Tracked\Removed.cs";
        await _service.OpenOrUpdateDocumentAsync(filePath, "class C { }");

        Assert.Contains(_service.GetAllTrackedDocumentFilePaths(),
            p => p.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        await _service.CloseDocumentAsync(filePath);

        Assert.DoesNotContain(_service.GetAllTrackedDocumentFilePaths(),
            p => p.Equals(filePath, StringComparison.OrdinalIgnoreCase));
    }

    // --- IsProjectConfigFile ---

    [Theory]
    [InlineData("MyApp.csproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("Directory.Build.targets", true)]
    [InlineData("Directory.Packages.props", true)]
    [InlineData("Program.cs", false)]
    [InlineData("appsettings.json", false)]
    [InlineData("", false)]
    public void WhenFileNameCheckedThenIsProjectConfigFileReturnsExpected(string fileName, bool expected)
    {
        string filePath = string.IsNullOrEmpty(fileName) ? "" : Path.Combine(@"C:\Repo\src", fileName);
        bool result = MainViewModel.IsProjectConfigFile(filePath);

        Assert.Equal(expected, result);
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
