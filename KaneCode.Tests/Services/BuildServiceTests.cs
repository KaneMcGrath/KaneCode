using KaneCode.Services;
using System;
using System.IO;


namespace KaneCode.Tests.Services;

public class BuildServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "KaneCodeBuildServiceTests_" + Guid.NewGuid().ToString("N"));

    public BuildServiceTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
    }

    [Fact]
    public void RunOutputDirectoryDefaultsToConfigurationAndTargetFrameworkOutputFolder()
    {
        string projectPath = Path.Combine(_tempDirectory, "Sample.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        string result = BuildService.GetRunOutputDirectory(projectPath, "Debug");

        Assert.Equal(Path.Combine(_tempDirectory, "bin", "Debug", "net8.0"), result);
        Assert.True(Directory.Exists(result));
    }

    [Fact]
    public void RunOutputDirectoryUsesFirstTargetFrameworkWhenProjectMultiTargets()
    {
        string projectPath = Path.Combine(_tempDirectory, "Sample.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        string result = BuildService.GetRunOutputDirectory(projectPath, "Release");

        Assert.Equal(Path.Combine(_tempDirectory, "bin", "Release", "net8.0"), result);
    }
}
