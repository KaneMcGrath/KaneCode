using KaneCode.Services;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.IO;
using Xunit;

namespace KaneCode.Tests.Services;

public class MSBuildProjectLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public MSBuildProjectLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KaneCodeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
    // --- GetDefaultLanguageVersionForTfm ---

    [Theory]
    [InlineData("net5.0", LanguageVersion.CSharp9)]
    [InlineData("net6.0", LanguageVersion.CSharp10)]
    [InlineData("net7.0", LanguageVersion.CSharp11)]
    [InlineData("net8.0", LanguageVersion.CSharp12)]
    [InlineData("net9.0", LanguageVersion.CSharp13)]
    public void WhenTfmIsModernDotNetThenLanguageVersionMatchesSdkDefault(string tfm, LanguageVersion expected)
    {
        LanguageVersion result = MSBuildProjectLoader.GetDefaultLanguageVersionForTfm(tfm);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net8.0-windows7.0")]
    public void WhenTfmHasPlatformSuffixThenLanguageVersionStillResolvesCorrectly(string tfm)
    {
        LanguageVersion result = MSBuildProjectLoader.GetDefaultLanguageVersionForTfm(tfm);

        Assert.Equal(LanguageVersion.CSharp12, result);
    }

    [Theory]
    [InlineData("net14.0")]
    [InlineData("net15.0")]
    public void WhenTfmIsFutureDotNetThenLanguageVersionIsPreview(string tfm)
    {
        LanguageVersion result = MSBuildProjectLoader.GetDefaultLanguageVersionForTfm(tfm);

        Assert.Equal(LanguageVersion.Preview, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenTfmIsNullOrEmptyThenLanguageVersionIsLatest(string? tfm)
    {
        LanguageVersion result = MSBuildProjectLoader.GetDefaultLanguageVersionForTfm(tfm);

        Assert.Equal(LanguageVersion.Latest, result);
    }

    [Theory]
    [InlineData("netstandard2.0")]
    [InlineData("netstandard2.1")]
    [InlineData("net48")]
    [InlineData("net472")]
    public void WhenTfmIsLegacyOrNetstandardThenLanguageVersionIsLatest(string tfm)
    {
        LanguageVersion result = MSBuildProjectLoader.GetDefaultLanguageVersionForTfm(tfm);

        Assert.Equal(LanguageVersion.Latest, result);
    }

    // --- GetFirstTargetFramework ---

    [Theory]
    [InlineData("net8.0;net6.0;netstandard2.0", "net8.0")]
    [InlineData("net9.0", "net9.0")]
    [InlineData("net8.0-windows;net8.0", "net8.0-windows")]
    public void WhenTargetFrameworksHasValuesThenFirstIsReturned(string targetFrameworks, string expected)
    {
        string? result = MSBuildProjectLoader.GetFirstTargetFramework(targetFrameworks);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenTargetFrameworksIsNullOrEmptyThenNullIsReturned(string? targetFrameworks)
    {
        string? result = MSBuildProjectLoader.GetFirstTargetFramework(targetFrameworks);

        Assert.Null(result);
    }

    [Fact]
    public void WhenTargetFrameworksHasExtraWhitespaceThenFirstIsTrimmed()
    {
        string? result = MSBuildProjectLoader.GetFirstTargetFramework("  net8.0 ; net6.0 ");

        Assert.Equal("net8.0", result);
    }

    [Fact]
    public void WhenTargetFrameworksHasEmptyEntriesThenTheyAreSkipped()
    {
        string? result = MSBuildProjectLoader.GetFirstTargetFramework(";;net8.0;;net6.0;;");

        Assert.Equal("net8.0", result);
    }

    [Fact]
    public void WhenTargetFrameworksIsOnlySemicolonsThenNullIsReturned()
    {
        string? result = MSBuildProjectLoader.GetFirstTargetFramework(";;;");

        Assert.Null(result);
    }

    // --- FindBestAnalyzerDirectory ---

    [Fact]
    public void WhenDotnetCsSubdirExistsThenItIsPreferred()
    {
        string analyzersDir = Path.Combine(_tempDir, "analyzers");
        string csDir = Path.Combine(analyzersDir, "dotnet", "cs");
        Directory.CreateDirectory(csDir);
        File.WriteAllText(Path.Combine(csDir, "MyAnalyzer.dll"), "");

        string? result = MSBuildProjectLoader.FindBestAnalyzerDirectory(analyzersDir);

        Assert.Equal(csDir, result);
    }

    [Fact]
    public void WhenOnlyDotnetSubdirExistsThenItIsReturned()
    {
        string analyzersDir = Path.Combine(_tempDir, "analyzers");
        string dotnetDir = Path.Combine(analyzersDir, "dotnet");
        Directory.CreateDirectory(dotnetDir);
        File.WriteAllText(Path.Combine(dotnetDir, "MyAnalyzer.dll"), "");

        string? result = MSBuildProjectLoader.FindBestAnalyzerDirectory(analyzersDir);

        Assert.Equal(dotnetDir, result);
    }

    [Fact]
    public void WhenOnlyRootAnalyzersDirHasDllsThenItIsReturned()
    {
        string analyzersDir = Path.Combine(_tempDir, "analyzers");
        Directory.CreateDirectory(analyzersDir);
        File.WriteAllText(Path.Combine(analyzersDir, "MyAnalyzer.dll"), "");

        string? result = MSBuildProjectLoader.FindBestAnalyzerDirectory(analyzersDir);

        Assert.Equal(analyzersDir, result);
    }

    [Fact]
    public void WhenAnalyzersDirIsEmptyThenNullIsReturned()
    {
        string analyzersDir = Path.Combine(_tempDir, "analyzers");
        Directory.CreateDirectory(analyzersDir);

        string? result = MSBuildProjectLoader.FindBestAnalyzerDirectory(analyzersDir);

        Assert.Null(result);
    }

    [Fact]
    public void WhenDotnetCsExistsButIsEmptyThenFallsBackToDotnet()
    {
        string analyzersDir = Path.Combine(_tempDir, "analyzers");
        Directory.CreateDirectory(Path.Combine(analyzersDir, "dotnet", "cs"));
        string dotnetDir = Path.Combine(analyzersDir, "dotnet");
        File.WriteAllText(Path.Combine(dotnetDir, "Analyzer.dll"), "");

        string? result = MSBuildProjectLoader.FindBestAnalyzerDirectory(analyzersDir);

        Assert.Equal(dotnetDir, result);
    }

    [Fact]
    public void WhenAllSubdirsEmptyThenNullIsReturned()
    {
        string analyzersDir = Path.Combine(_tempDir, "analyzers");
        Directory.CreateDirectory(Path.Combine(analyzersDir, "dotnet", "cs"));
        Directory.CreateDirectory(Path.Combine(analyzersDir, "dotnet", "vb"));

        string? result = MSBuildProjectLoader.FindBestAnalyzerDirectory(analyzersDir);

        Assert.Null(result);
    }

    // --- ResolvePackageAnalyzers ---

    [Fact]
    public void WhenPackageDoesNotExistThenNoAnalyzersAreReturned()
    {
        List<AnalyzerReference> references = [];
        HashSet<string> addedPaths = new(StringComparer.OrdinalIgnoreCase);

        MSBuildProjectLoader.ResolvePackageAnalyzers(
            references, addedPaths, "NonExistent.Package", "99.99.99", "net8.0");

        Assert.Empty(references);
    }

    // --- CollectEditorConfigFiles ---

    [Fact]
    public void WhenEditorConfigExistsInProjectDirThenItIsCollected()
    {
        string projectDir = Path.Combine(_tempDir, "src", "MyProject");
        Directory.CreateDirectory(projectDir);
        string editorConfigPath = Path.Combine(projectDir, ".editorconfig");
        File.WriteAllText(editorConfigPath, "root = true\n[*.cs]\nindent_size = 4");

        IReadOnlyList<string> result = MSBuildProjectLoader.CollectEditorConfigFiles(projectDir);

        Assert.Single(result);
        Assert.Equal(editorConfigPath, result[0]);
    }

    [Fact]
    public void WhenEditorConfigExistsInParentDirsThenAllAreCollected()
    {
        string rootDir = Path.Combine(_tempDir, "repo");
        string srcDir = Path.Combine(rootDir, "src");
        string projectDir = Path.Combine(srcDir, "MyProject");
        Directory.CreateDirectory(projectDir);

        // Parent editorconfig without root = true
        string parentConfig = Path.Combine(srcDir, ".editorconfig");
        File.WriteAllText(parentConfig, "[*.cs]\nindent_size = 4");

        // Root editorconfig with root = true
        string rootConfig = Path.Combine(rootDir, ".editorconfig");
        File.WriteAllText(rootConfig, "root = true\n[*.cs]\nindent_size = 2");

        IReadOnlyList<string> result = MSBuildProjectLoader.CollectEditorConfigFiles(projectDir);

        // Should collect both: parent first (closer to project), root second
        Assert.Equal(2, result.Count);
        Assert.Equal(parentConfig, result[0]);
        Assert.Equal(rootConfig, result[1]);
    }

    [Fact]
    public void WhenRootEditorConfigFoundThenWalkStops()
    {
        string outerDir = Path.Combine(_tempDir, "outer");
        string innerDir = Path.Combine(outerDir, "inner");
        string projectDir = Path.Combine(innerDir, "project");
        Directory.CreateDirectory(projectDir);

        // This one should NOT be collected (above the root = true file)
        string outerConfig = Path.Combine(outerDir, ".editorconfig");
        File.WriteAllText(outerConfig, "[*.cs]\nindent_size = 8");

        // Root editorconfig stops the walk
        string innerConfig = Path.Combine(innerDir, ".editorconfig");
        File.WriteAllText(innerConfig, "root = true\n[*.cs]\nindent_size = 4");

        IReadOnlyList<string> result = MSBuildProjectLoader.CollectEditorConfigFiles(projectDir);

        Assert.Single(result);
        Assert.Equal(innerConfig, result[0]);
    }

    [Fact]
    public void WhenNoEditorConfigExistsThenEmptyListIsReturned()
    {
        string projectDir = Path.Combine(_tempDir, "empty_project");
        Directory.CreateDirectory(projectDir);

        IReadOnlyList<string> result = MSBuildProjectLoader.CollectEditorConfigFiles(projectDir);

        Assert.Empty(result);
    }

    [Fact]
    public void WhenProjectDirIsNullOrEmptyThenEmptyListIsReturned()
    {
        Assert.Empty(MSBuildProjectLoader.CollectEditorConfigFiles(""));
        Assert.Empty(MSBuildProjectLoader.CollectEditorConfigFiles("   "));
    }

    [Fact]
    public void WhenRootEqualsTrue_CaseInsensitive_ThenWalkStops()
    {
        string parentDir = Path.Combine(_tempDir, "parent");
        string projectDir = Path.Combine(parentDir, "child");
        Directory.CreateDirectory(projectDir);

        // Should NOT be collected
        string parentConfig = Path.Combine(parentDir, ".editorconfig");
        File.WriteAllText(parentConfig, "[*.cs]\nindent_size = 8");

        // root = true with different casing/spacing
        string childConfig = Path.Combine(projectDir, ".editorconfig");
        File.WriteAllText(childConfig, "  ROOT  =  TRUE  \n[*.cs]\nindent_size = 4");

        IReadOnlyList<string> result = MSBuildProjectLoader.CollectEditorConfigFiles(projectDir);

        Assert.Single(result);
        Assert.Equal(childConfig, result[0]);
    }

    [Fact]
    public void WhenRootTrueIsInsideSectionThenItIsNotTreatedAsRoot()
    {
        string parentDir = Path.Combine(_tempDir, "parent2");
        string projectDir = Path.Combine(parentDir, "child2");
        Directory.CreateDirectory(projectDir);

        // Should be collected because root=true is inside a section (invalid position)
        string parentConfig = Path.Combine(parentDir, ".editorconfig");
        File.WriteAllText(parentConfig, "root = true\n[*.cs]\nindent_size = 2");

        // root = true after a section header — not valid preamble
        string childConfig = Path.Combine(projectDir, ".editorconfig");
        File.WriteAllText(childConfig, "[*.cs]\nroot = true\nindent_size = 4");

        IReadOnlyList<string> result = MSBuildProjectLoader.CollectEditorConfigFiles(projectDir);

        // Child is not root (root=true is after [section]), parent has root=true in preamble
        Assert.Equal(2, result.Count);
        Assert.Equal(childConfig, result[0]);
        Assert.Equal(parentConfig, result[1]);
    }
}
