using KaneCode.Services;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace KaneCode.Tests.Services;

public class MSBuildProjectLoaderTests
{
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
}
