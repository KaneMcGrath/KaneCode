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
    /// Returns the valid target framework monikers for the given template's
    /// <c>Framework</c> choice parameter, sorted newest-first.
    /// Returns an empty list when the template has no <c>Framework</c> parameter.
    /// </summary>
    internal static IReadOnlyList<FrameworkChoice> GetFrameworkChoices(ITemplateInfo template)
    {
        var param = template.GetChoiceParameter("Framework");
        if (param?.Choices is null || param.Choices.Count == 0)
        {
            return [];
        }

        return param.Choices
            .Select(kv => new FrameworkChoice(kv.Key, kv.Value.DisplayName))
            .OrderByDescending(f => f.Moniker, StringComparer.OrdinalIgnoreCase)
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
                virtualizeConfiguration: false,
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
    /// Finds the installed .NET SDK's template directories and installs
    /// the template <c>.nupkg</c> packages into the engine.
    /// </summary>
    private static async Task InstallSdkTemplatePackagesAsync(
        Bootstrapper bootstrapper,
        CancellationToken cancellationToken)
    {
        var packagePaths = FindSdkTemplatePackages();
        if (packagePaths.Count == 0)
        {
            Debug.WriteLine("[TemplateEngine] No SDK template packages found.");
            Debug.WriteLine($"[TemplateEngine] DOTNET_ROOT={Environment.GetEnvironmentVariable("DOTNET_ROOT")}");
            Debug.WriteLine($"[TemplateEngine] Resolved root={FindDotnetRoot()}");
            return;
        }

        Debug.WriteLine($"[TemplateEngine] Installing {packagePaths.Count} template package(s)...");

        var requests = packagePaths
            .Select(path => new InstallRequest(path))
            .ToList();

        var results = await bootstrapper.InstallTemplatePackagesAsync(
            requests,
            InstallationScope.Global,
            cancellationToken).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (result.Success)
            {
                Debug.WriteLine($"[TemplateEngine]   OK: {result.InstallRequest.PackageIdentifier}");
            }
            else
            {
                Debug.WriteLine($"[TemplateEngine]   FAIL: {result.InstallRequest.PackageIdentifier} — {result.ErrorMessage}");
            }
        }
    }

    /// <summary>
    /// Locates the <c>.nupkg</c> template packages shipped with the installed .NET SDK.
    /// Scans three locations in order of preference:
    /// <list type="number">
    ///   <item><c>&lt;dotnetRoot&gt;/templates/&lt;version&gt;/</c> — modern layout (.NET 8+)</item>
    ///   <item><c>&lt;dotnetRoot&gt;/template-packs/</c> — workload/extra template packs</item>
    ///   <item><c>&lt;dotnetRoot&gt;/sdk/&lt;version&gt;/Templates/</c> — legacy layout</item>
    /// </list>
    /// </summary>
    private static List<string> FindSdkTemplatePackages()
    {
        var dotnetRoot = FindDotnetRoot();
        if (dotnetRoot is null)
        {
            return [];
        }

        var packages = new List<string>();

        // Modern layout: <dotnetRoot>/templates/<version>/*.nupkg
        var templatesRoot = Path.Combine(dotnetRoot, "templates");
        if (Directory.Exists(templatesRoot))
        {
            // Pick the latest version directory
            var latestTemplateDir = Directory.EnumerateDirectories(templatesRoot)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.Length > 0 && char.IsDigit(name[0]);
                })
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (latestTemplateDir is not null)
            {
                packages.AddRange(Directory.EnumerateFiles(latestTemplateDir, "*.nupkg"));
            }
        }

        // Workload / extra templates: <dotnetRoot>/template-packs/*.nupkg
        var templatePacksDir = Path.Combine(dotnetRoot, "template-packs");
        if (Directory.Exists(templatePacksDir))
        {
            packages.AddRange(Directory.EnumerateFiles(templatePacksDir, "*.nupkg"));
        }

        // Legacy layout: <dotnetRoot>/sdk/<version>/Templates/*.nupkg
        if (packages.Count == 0)
        {
            var sdkDir = Path.Combine(dotnetRoot, "sdk");
            if (Directory.Exists(sdkDir))
            {
                var latestSdk = Directory.EnumerateDirectories(sdkDir)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return name.Length > 0 && char.IsDigit(name[0]);
                    })
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (latestSdk is not null)
                {
                    var legacyDir = Path.Combine(latestSdk, "Templates");
                    if (Directory.Exists(legacyDir))
                    {
                        packages.AddRange(Directory.EnumerateFiles(legacyDir, "*.nupkg"));
                    }
                }
            }
        }

        return packages;
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

/// <summary>
/// A target framework option exposed by a template's <c>Framework</c> choice parameter.
/// </summary>
/// <param name="Moniker">The value passed to the template engine (e.g. <c>net8.0</c>).</param>
/// <param name="DisplayName">Human-readable name from the template (e.g. <c>.NET 8.0</c>), may be empty.</param>
internal sealed record FrameworkChoice(string Moniker, string? DisplayName)
{
    /// <summary>Shows e.g. <c>.NET 8.0 (net8.0)</c> or just <c>net8.0</c> when no display name exists.</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(DisplayName) ? Moniker : $"{DisplayName} ({Moniker})";
}
