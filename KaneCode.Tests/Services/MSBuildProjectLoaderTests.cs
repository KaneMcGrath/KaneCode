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
}
