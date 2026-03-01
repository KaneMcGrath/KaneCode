using System.Diagnostics;
using System.IO;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Installer;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.TemplateEngine.IDE;
using Microsoft.TemplateEngine.Utils;
using EdgeHost = Microsoft.TemplateEngine.Edge.DefaultTemplateEngineHost;

namespace KaneCode.Services;

/// <summary>
/// Uses the <c>Microsoft.TemplateEngine</c> API to discover installed .NET SDK templates
/// and scaffold new projects without parsing CLI output.
/// Solution creation still uses a minimal <c>dotnet</c> CLI invocation.
/// </summary>
internal sealed class TemplateEngineService : IDisposable
{
    private const string HostIdentifier = "KaneCode";
    private const string HostVersion = "1.0.0";

    /// <summary>
    /// Environment variables set by MSBuildLocator that must be removed from child
    /// processes so that <c>dotnet</c> resolves its own SDK.
    /// </summary>
    private static readonly string[] s_msBuildEnvironmentVariables =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath"
    ];

    private Bootstrapper? _bootstrapper;
    private bool _sdkTemplatesInstalled;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Discovers all installed project templates that target C#.
    /// </summary>
    internal async Task<IReadOnlyList<ITemplateInfo>> GetProjectTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var bootstrapper = await GetBootstrapperAsync(cancellationToken).ConfigureAwait(false);
        var allTemplates = await bootstrapper.GetTemplatesAsync(cancellationToken).ConfigureAwait(false);

        return allTemplates
            .Where(t => string.Equals(t.GetTemplateType(), "project", StringComparison.OrdinalIgnoreCase))
            .Where(t =>
            {
                var lang = t.GetLanguage();
                return string.IsNullOrEmpty(lang)
                    || lang.Contains("C#", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Creates a new project from the specified template.
    /// </summary>
    /// <returns>The output directory where files were created.</returns>
    internal async Task<string> CreateProjectAsync(
        ITemplateInfo template,
        string projectName,
        string outputDirectory,
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException("Project name is required.", nameof(projectName));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        }

        var bootstrapper = await GetBootstrapperAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            parameters["Framework"] = targetFramework;
        }

        var result = await bootstrapper.CreateAsync(
            template,
            projectName,
            outputDirectory,
            parameters,
            baselineName: null,
            cancellationToken).ConfigureAwait(false);

        if (result.Status != CreationResultStatus.Success)
        {
            throw new InvalidOperationException(
                $"Template instantiation failed ({result.Status}): {result.ErrorMessage}");
        }

        return result.OutputBaseDirectory ?? outputDirectory;
    }

    /// <summary>
    /// Creates a solution file via <c>dotnet new sln</c> in the given directory,
    /// adds the discovered <c>.csproj</c> to it, and returns the created solution path.
    /// </summary>
    internal async Task<string> CreateSolutionAsync(
        string solutionName,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
        {
            throw new ArgumentException("Solution name is required.", nameof(solutionName));
        }

        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        }

        await RunDotnetAsync(
            $"new sln --name \"{solutionName}\"",
            projectDirectory,
            cancellationToken).ConfigureAwait(false);

        var solutionPath = ResolveCreatedSolutionPath(projectDirectory, solutionName);

        var csprojFile = Directory.EnumerateFiles(projectDirectory, "*.csproj").FirstOrDefault();
        if (!string.IsNullOrEmpty(csprojFile))
        {
            await RunDotnetAsync(
                $"sln \"{solutionPath}\" add \"{csprojFile}\"",
                projectDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        return solutionPath;
    }

    public void Dispose()
    {
        _bootstrapper?.Dispose();
        _initLock.Dispose();
    }

    // ── Bootstrapper lifecycle ─────────────────────────────────────────

    /// <summary>
    /// Gets or lazily creates the <see cref="Bootstrapper"/>, installing SDK template
    /// packages on first use.
    /// </summary>
    private async Task<Bootstrapper> GetBootstrapperAsync(CancellationToken cancellationToken)
    {
        if (_bootstrapper is not null && _sdkTemplatesInstalled)
        {
            return _bootstrapper;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bootstrapper is not null && _sdkTemplatesInstalled)
            {
                return _bootstrapper;
            }

            var host = new EdgeHost(HostIdentifier, HostVersion);
            _bootstrapper = new Bootstrapper(
                host,
                virtualizeConfiguration: true,
                loadDefaultComponents: true,
                hostSettingsLocation: null);

            await InstallSdkTemplatePackagesAsync(_bootstrapper, cancellationToken)
                .ConfigureAwait(false);
            _sdkTemplatesInstalled = true;

            return _bootstrapper;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ── SDK template discovery ─────────────────────────────────────────

    /// <summary>
    /// Finds the installed .NET SDK's <c>Templates</c> directory and installs
    /// the template <c>.nupkg</c> packages into the engine.
    /// </summary>
    private static async Task InstallSdkTemplatePackagesAsync(
        Bootstrapper bootstrapper,
        CancellationToken cancellationToken)
    {
        var packagePaths = FindSdkTemplatePackages();
        if (packagePaths.Count == 0)
        {
            return;
        }

        var requests = packagePaths
            .Select(path => new InstallRequest(path))
            .ToList();

        await bootstrapper.InstallTemplatePackagesAsync(
            requests,
            InstallationScope.Global,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Locates the <c>.nupkg</c> template packages shipped with the installed .NET SDK.
    /// </summary>
    private static List<string> FindSdkTemplatePackages()
    {
        var dotnetRoot = FindDotnetRoot();
        if (dotnetRoot is null)
        {
            return [];
        }

        var sdkDir = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkDir))
        {
            return [];
        }

        // Pick the latest SDK version directory (directories starting with a digit)
        var latestSdk = Directory.EnumerateDirectories(sdkDir)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return name.Length > 0 && char.IsDigit(name[0]);
            })
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (latestSdk is null)
        {
            return [];
        }

        var templatesDir = Path.Combine(latestSdk, "Templates");
        return Directory.Exists(templatesDir)
            ? Directory.EnumerateFiles(templatesDir, "*.nupkg").ToList()
            : [];
    }

    /// <summary>
    /// Finds the root directory of the .NET installation by checking
    /// environment variables, the system PATH, and common install locations.
    /// </summary>
    private static string? FindDotnetRoot()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            return root;
        }

        root = Environment.GetEnvironmentVariable("DOTNET_INSTALL_DIR");
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            return root;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dotnetExe = Path.Combine(dir, "dotnet.exe");
            if (File.Exists(dotnetExe))
            {
                return dir;
            }
        }

        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet");
        if (Directory.Exists(programFiles))
        {
            return programFiles;
        }

        return null;
    }

    // ── Solution helpers ───────────────────────────────────────────────

    private static string ResolveCreatedSolutionPath(string projectDirectory, string solutionName)
    {
        var slnPath = Path.Combine(projectDirectory, solutionName + ".sln");
        if (File.Exists(slnPath))
        {
            return slnPath;
        }

        var slnxPath = Path.Combine(projectDirectory, solutionName + ".slnx");
        if (File.Exists(slnxPath))
        {
            return slnxPath;
        }

        throw new InvalidOperationException(
            $"Solution file was not created in '{projectDirectory}'. " +
            $"Expected '{solutionName}.sln' or '{solutionName}.slnx'.");
    }

    // ── Minimal CLI for solution operations ────────────────────────────

    private static async Task<string> RunDotnetAsync(
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
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

        foreach (var key in s_msBuildEnvironmentVariables)
        {
            startInfo.Environment.Remove(key);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"dotnet {arguments} failed (exit code {process.ExitCode}):\n{message.Trim()}");
        }

        return stdout;
    }
}
