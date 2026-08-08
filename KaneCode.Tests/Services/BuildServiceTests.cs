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

    [Fact]
    public async Task ScopedOutputCallbackDoesNotCaptureEventsFromSupersededProcess()
    {
        // Regression test for a bug where a successful build was reported as a failure.
        // Every new invocation cancels the previous process, and the previous process's
        // cancellation events ("Build/Run cancelled." + exit code -1) used to leak into the
        // new invocation's capture when consumers subscribed to the global
        // OutputReceived/ProcessExited events. The scoped onOutput callback must only
        // receive output from its own invocation, and the returned exit code must be the
        // exit code of its own process.
        using var buildService = new BuildService();

        string longRunningDir = Path.Combine(_tempDirectory, "LongRunning");
        Directory.CreateDirectory(longRunningDir);
        File.WriteAllText(Path.Combine(longRunningDir, "LongRunning.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(longRunningDir, "Program.cs"), """
            using System;
            using System.Threading;
            Console.WriteLine("LONG_RUNNING_STARTED");
            while (true)
            {
                Thread.Sleep(1000);
            }
            """);
        string longRunningProject = Path.Combine(longRunningDir, "LongRunning.csproj");

        string quickDir = Path.Combine(_tempDirectory, "Quick");
        Directory.CreateDirectory(quickDir);
        File.WriteAllText(Path.Combine(quickDir, "Quick.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(quickDir, "Program.cs"), """
            using System;
            Console.WriteLine("QUICK_DONE");
            """);
        string quickProject = Path.Combine(quickDir, "Quick.csproj");

        var longRunningLines = new List<string>();
        var quickLines = new List<string>();

        // Start a process that stays alive until it is cancelled by a newer invocation.
        Task<int> longRunningTask = buildService.RunProjectAsync(
            longRunningProject,
            cancellationToken: CancellationToken.None,
            onOutput: longRunningLines.Add);

        // Wait until the first process is definitely running.
        var startedDeadline = DateTime.UtcNow.AddSeconds(120);
        while (!longRunningLines.Contains("LONG_RUNNING_STARTED"))
        {
            if (DateTime.UtcNow > startedDeadline)
            {
                throw new TimeoutException("Long-running process did not start in time.");
            }

            await Task.Delay(100);
        }

        // The new invocation cancels the previous one. Its scoped capture must not include
        // the previous process's cancellation events, and its exit code must be its own.
        int quickExitCode = await buildService.BuildAsync(
            quickProject,
            cancellationToken: CancellationToken.None,
            onOutput: quickLines.Add);

        Assert.Equal(0, quickExitCode);
        Assert.DoesNotContain(quickLines, line => line.Contains("Build/Run cancelled.", StringComparison.Ordinal));
        Assert.Contains(quickLines, line => line.Contains("Build succeeded", StringComparison.Ordinal));

        // The superseded process's own capture still reports its cancellation.
        int longRunningExitCode = await longRunningTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(-1, longRunningExitCode);
        Assert.Contains(longRunningLines, line => line.Contains("Build/Run cancelled.", StringComparison.Ordinal));
    }
}
