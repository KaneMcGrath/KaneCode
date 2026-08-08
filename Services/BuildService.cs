using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Xml.Linq;

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

    /// <summary>
    /// Raised for each stdout/stderr line produced by a process.
    /// This is a global broadcast intended for UI streaming (e.g. the Build Output panel).
    /// Callers that need output for a specific invocation should use the <c>onOutput</c>
    /// parameter on the run methods instead: the broadcast also carries events raised by
    /// a previous process that this invocation cancels, so subscribing to it from a
    /// per-invocation capture would let stale output leak into the result.
    /// </summary>
    public event Action<string>? OutputReceived;

    /// <summary>
    /// Raised when a process exits with its exit code.
    /// This is a global broadcast intended for UI status (e.g. build summary text).
    /// Callers that need the exit code of a specific invocation should use the return
    /// value of the run methods instead: the broadcast also carries events raised by a
    /// previous process that this invocation cancels, so a stale exit code (such as -1
    /// from a cancelled earlier build) could otherwise be mistaken for the current run.
    /// </summary>
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
    /// Runs <c>dotnet clean</c> in the given project/solution directory.
    /// </summary>
    /// <param name="projectOrSolutionPath">Path to the project or solution to clean.</param>
    /// <param name="configuration">Optional build configuration (Debug/Release). Defaults to all configurations if not specified.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onOutput">Optional callback invoked for each output line produced by this
    /// invocation. It only receives output from this run — never stale output from a previous
    /// process that this invocation cancelled.</param>
    /// <returns>The process exit code (0 = success, -1 = cancelled).</returns>
    public async Task<int> CleanAsync(
        string projectOrSolutionPath,
        string? configuration = null,
        CancellationToken cancellationToken = default,
        Action<string>? onOutput = null)
    {
        var directory = GetWorkingDirectory(projectOrSolutionPath);
        var arguments = $"clean \"{projectOrSolutionPath}\"";
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            arguments += $" --configuration {configuration}";
        }
        return await RunDotnetAsync(arguments, directory, cancellationToken, onOutput: onOutput).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>dotnet build</c> in the given project/solution directory.
    /// </summary>
    /// <param name="projectOrSolutionPath">Path to the project or solution to build.</param>
    /// <param name="configuration">Optional build configuration (Debug/Release).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onOutput">Optional callback invoked for each output line produced by this
    /// invocation. It only receives output from this run — never stale output from a previous
    /// process that this invocation cancelled.</param>
    /// <returns>The process exit code (0 = success, -1 = cancelled).</returns>
    public async Task<int> BuildAsync(
        string projectOrSolutionPath,
        string? configuration = null,
        CancellationToken cancellationToken = default,
        Action<string>? onOutput = null)
    {
        var directory = GetWorkingDirectory(projectOrSolutionPath);
        var arguments = $"build \"{projectOrSolutionPath}\"";
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            arguments += $" --configuration {configuration}";
        }
        return await RunDotnetAsync(arguments, directory, cancellationToken, onOutput: onOutput).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>dotnet run</c> in the given project directory.
    /// </summary>
    public async Task<int> RunAsync(string projectPath, CancellationToken cancellationToken = default, Action<string>? onOutput = null)
    {
        var directory = GetWorkingDirectory(projectPath);
        var arguments = $"run --project \"{projectPath}\"";
        return await RunDotnetAsync(arguments, directory, cancellationToken, onOutput: onOutput).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>dotnet run</c> with optional program arguments and build configuration.
    /// </summary>
    /// <param name="projectPath">Path to the project to run.</param>
    /// <param name="programArguments">Optional command-line arguments to pass to the program.</param>
    /// <param name="configuration">Optional build configuration (Debug/Release).</param>
    /// <param name="workingDirectory">Optional working directory for the process.</param>
    /// <param name="environmentVariables">Optional environment variables for the process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onOutput">Optional callback invoked for each output line produced by this
    /// invocation. It only receives output from this run — never stale output from a previous
    /// process that this invocation cancelled.</param>
    /// <returns>The process exit code (0 = success, -1 = cancelled).</returns>
    public async Task<int> RunProjectAsync(
        string projectPath,
        string? programArguments = null,
        string? configuration = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default,
        Action<string>? onOutput = null)
    {
        string directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? GetRunOutputDirectory(projectPath, configuration)
            : Path.GetFullPath(workingDirectory);

        var args = new System.Text.StringBuilder();
        args.Append("run --project \"");
        args.Append(projectPath);
        args.Append('"');

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            args.Append(" --configuration ");
            args.Append(configuration);
        }

        if (!string.IsNullOrWhiteSpace(programArguments))
        {
            args.Append(" -- ");
            args.Append(programArguments);
        }

        return await RunDotnetAsync(args.ToString(), directory, cancellationToken, environmentVariables, onOutput).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>dotnet test</c> with the specified options.
    /// </summary>
    /// <param name="projectOrSolutionPath">Path to the test project or solution.</param>
    /// <param name="filter">Optional test filter expression (e.g. "FullyQualifiedName~MyTest").</param>
    /// <param name="configuration">Optional configuration (Debug/Release).</param>
    /// <param name="framework">Optional target framework moniker (e.g. "net8.0").</param>
    /// <param name="verbosity">Optional verbosity level (quiet/minimal/normal/detailed/diagnostic).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onOutput">Optional callback invoked for each output line produced by this
    /// invocation. It only receives output from this run — never stale output from a previous
    /// process that this invocation cancelled.</param>
    /// <returns>The process exit code (0 = success, -1 = cancelled).</returns>
    public async Task<int> TestAsync(
        string projectOrSolutionPath,
        string? filter = null,
        string? configuration = null,
        string? framework = null,
        string? verbosity = null,
        CancellationToken cancellationToken = default,
        Action<string>? onOutput = null)
    {
        var directory = GetWorkingDirectory(projectOrSolutionPath);
        var args = new System.Text.StringBuilder();
        args.Append("test \"");
        args.Append(projectOrSolutionPath);
        args.Append('"');

        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.Append(" --filter \"");
            args.Append(filter);
            args.Append('"');
        }

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            args.Append(" --configuration ");
            args.Append(configuration);
        }

        if (!string.IsNullOrWhiteSpace(framework))
        {
            args.Append(" --framework ");
            args.Append(framework);
        }

        if (!string.IsNullOrWhiteSpace(verbosity))
        {
            args.Append(" --verbosity ");
            args.Append(verbosity);
        }

        return await RunDotnetAsync(args.ToString(), directory, cancellationToken, onOutput: onOutput).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the currently running build/run/test process, if any.
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

    private async Task<int> RunDotnetAsync(
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Action<string>? onOutput = null)
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
        foreach (string key in s_msBuildEnvironmentVariables)
        {
            startInfo.Environment.Remove(key);
        }

        if (environmentVariables is not null)
        {
            foreach (KeyValuePair<string, string> variable in environmentVariables)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data);
                onOutput?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data);
                onOutput?.Invoke(e.Data);
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

            int exitCode = process.ExitCode;
            ProcessExited?.Invoke(exitCode);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            // Kill the process tree when cancellation occurs (timeout or user cancel).
            // WaitForExitAsync threw, so the process is still running.
            if (process is not null && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited between the check and the kill call
                }
            }

            OutputReceived?.Invoke("Build/Run cancelled.");
            onOutput?.Invoke("Build/Run cancelled.");
            ProcessExited?.Invoke(-1);
            return -1;
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

    /// <summary>
    /// Gets the working directory used for a project launched without an explicit
    /// launch-profile working directory.  This intentionally matches the directory
    /// containing the built application rather than the project directory.  A
    /// launched application normally starts with its output directory as its
    /// current directory (and files copied to the output directory are commonly
    /// accessed using relative paths).
    /// </summary>
    internal static string GetRunOutputDirectory(string projectPath, string? configuration)
    {
        string projectDirectory = GetWorkingDirectory(projectPath);
        string? targetFramework = GetTargetFramework(projectPath);
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            // Keep the old, safe fallback for projects whose target framework
            // cannot be read.  dotnet will still report useful build errors.
            return projectDirectory;
        }

        string outputDirectory = Path.Combine(
            projectDirectory,
            "bin",
            string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim(),
            targetFramework);

        // ProcessStartInfo requires the working directory to exist before dotnet
        // has a chance to build the project.
        Directory.CreateDirectory(outputDirectory);
        return outputDirectory;
    }

    private static string? GetTargetFramework(string projectPath)
    {
        if (!File.Exists(projectPath))
        {
            return null;
        }

        try
        {
            XDocument document = XDocument.Load(projectPath);
            XElement? framework = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("TargetFramework", StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(framework?.Value))
            {
                return framework.Value.Trim();
            }

            XElement? frameworks = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("TargetFrameworks", StringComparison.Ordinal));
            return frameworks?.Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    private static string GetWorkingDirectory(string projectOrSolutionPath)
    {
        if (Directory.Exists(projectOrSolutionPath))
        {
            return Path.GetFullPath(projectOrSolutionPath);
        }

        string? dir = Path.GetDirectoryName(projectOrSolutionPath);
        if (string.IsNullOrEmpty(dir))
        {
            return Directory.GetCurrentDirectory();
        }

        // Resolve relative paths to absolute so the process working directory is always valid.
        return Path.GetFullPath(dir);
    }

    public void Dispose()
    {
        Cancel();
    }
}
