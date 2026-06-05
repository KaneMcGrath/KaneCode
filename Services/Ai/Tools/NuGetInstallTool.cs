using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that installs a NuGet package into the loaded project's .csproj file
/// by adding or updating a PackageReference.
/// </summary>
internal sealed class NuGetInstallTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "packageId": {
                    "type": "string",
                    "description": "The NuGet package ID to install (e.g. 'Newtonsoft.Json')."
                },
                "version": {
                    "type": "string",
                    "description": "The version to install. If omitted, the caller should first use nuget_info or nuget_search to find the latest version."
                }
            },
            "required": ["packageId", "version"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectPathProvider;

    public NuGetInstallTool(Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "nuget_install";

    public string Category => "NuGet";

    public string Description =>
        "Installs a NuGet package into the currently loaded project by adding a PackageReference " +
        "to the .csproj file. Requires both the package ID and the exact version to install. " +
        "Use nuget_search or nuget_info first to find the correct version. " +
        "This modifies the project file on disk.";

    public bool RequiresConfirmation => true;

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var projectPath = _projectPathProvider();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return Task.FromResult(ToolCallResult.Fail("No project or solution is currently loaded. Open a project first."));
        }

        // Resolve the actual .csproj path
        string? csprojPath = null;

        if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            csprojPath = projectPath;
        }
        else if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                 projectPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            // For a solution, find the first .csproj
            var solutionDir = Path.GetDirectoryName(projectPath);
            if (solutionDir is not null && Directory.Exists(solutionDir))
            {
                csprojPath = Directory.EnumerateFiles(solutionDir, "*.csproj", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
            }
        }
        else if (Directory.Exists(projectPath))
        {
            csprojPath = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath))
        {
            return Task.FromResult(ToolCallResult.Fail(
                $"Could not find a .csproj file. The loaded path is '{projectPath}'. " +
                "Open a .NET project or solution first."));
        }

        if (!arguments.TryGetProperty("packageId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: 'packageId'."));
        }

        var packageId = idElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return Task.FromResult(ToolCallResult.Fail("Package ID cannot be empty."));
        }

        if (!arguments.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: 'version'. Use nuget_search or nuget_info to find the latest version."));
        }

        var versionStr = versionElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(versionStr))
        {
            return Task.FromResult(ToolCallResult.Fail("Version cannot be empty."));
        }

        if (!NuGet.Versioning.NuGetVersion.TryParse(versionStr, out var version))
        {
            return Task.FromResult(ToolCallResult.Fail(
                $"Invalid version format: '{versionStr}'. Expected a valid NuGet version like '13.0.3'."));
        }

        try
        {
            var success = NuGetService.InstallPackage(csprojPath, packageId, version);

            if (success)
            {
                return Task.FromResult(ToolCallResult.Ok(
                    $"Successfully installed {packageId} v{version.ToFullString()} into '{Path.GetFileName(csprojPath)}'.\n" +
                    $"A project file change was detected. Run 'build' to restore packages and rebuild the project."));
            }
            else
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"Package '{packageId}' is already installed in '{Path.GetFileName(csprojPath)}'. " +
                    "Use nuget_install with a different version or check installed packages."));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Failed to install {packageId}: {ex.Message}"));
        }
    }
}
