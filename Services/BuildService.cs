using System.Diagnostics;
using System.IO;

namespace KaneCode.Services;

/// <summary>
/// Shells out to <c>dotnet build</c> / <c>dotnet run</c> and streams output line-by-line.
/// </summary>
internal sealed class BuildService : IDisposable
{
    private Process? _activeProcess;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    /// <summary>
    /// Environment variables set by MSBuildLocator that must be removed from child
    /// processes so that <c>dotnet build</c>/<c>dotnet run</c> resolves its own SDK.
    /// </summary>
    private static readonly string[] s_msBuildEnvironmentVariables =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath"
    ];

    /// <summary>Raised for each stdout/stderr line produced by the process.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised when the process exits with its exit code.</summary>
    public event Action<int>? ProcessExited;

    /// <summary>Whether a build or run process is currently active.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _activeProcess is not null && !_activeProcess.HasExited;
            }
        }
    }

    /// <summary>
    /// Runs <c>dotnet build</c> in the given project/solution directory.
    /// </summary>
    public async Task BuildAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default)
    {
        var directory = GetWorkingDirectory(projectOrSolutionPath);
        var arguments = $"build \"{projectOrSolutionPath}\"";
        await RunDotnetAsync(arguments, directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>dotnet run</c> in the given project directory.
    /// </summary>
    public async Task RunAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var directory = GetWorkingDirectory(projectPath);
        var arguments = $"run --project \"{projectPath}\"";
        await RunDotnetAsync(arguments, directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the currently running build/run process, if any.
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();

            if (_activeProcess is not null && !_activeProcess.HasExited)
            {
                try
                {
                    _activeProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited
                }
            }
        }
    }

    private async Task RunDotnetAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        Cancel();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _cts = cts;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // MSBuildLocator sets these in the host process; if the child inherits them
        // it loads mismatched MSBuild assemblies and fails with MissingMethodException.
        foreach (var key in s_msBuildEnvironmentVariables)
        {
            startInfo.Environment.Remove(key);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data);
            }
        };

        lock (_lock)
        {
            _activeProcess = process;
        }

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            ProcessExited?.Invoke(process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            OutputReceived?.Invoke("Build/Run cancelled.");
            ProcessExited?.Invoke(-1);
        }
        finally
        {
            lock (_lock)
            {
                if (_activeProcess == process)
                {
                    _activeProcess = null;
                }

                if (_cts == cts)
                {
                    _cts = null;
                }
            }

            cts.Dispose();
            process.Dispose();
        }
    }

    private static string GetWorkingDirectory(string projectOrSolutionPath)
    {
        if (Directory.Exists(projectOrSolutionPath))
        {
            return projectOrSolutionPath;
        }

        return Path.GetDirectoryName(projectOrSolutionPath) ?? Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        Cancel();
    }
}
