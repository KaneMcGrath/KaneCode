using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that uninstalls a NuGet package from the loaded project's .csproj file
/// by removing its PackageReference.
/// </summary>
internal sealed class NuGetUninstallTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "packageId": {
                    "type": "string",
                    "description": "The NuGet package ID to uninstall (e.g. 'Newtonsoft.Json')."
                }
            },
            "required": ["packageId"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectPathProvider;

    public NuGetUninstallTool(Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "nuget_uninstall";

    public string Category => "NuGet";

    public string Description =>
        "Uninstalls a NuGet package from the currently loaded project by removing its " +
        "PackageReference from the .csproj file. This modifies the project file on disk. " +
        "Use nuget_list_installed to see which packages are currently installed.";

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

        try
        {
            var success = NuGetService.UninstallPackage(csprojPath, packageId);

            if (success)
            {
                return Task.FromResult(ToolCallResult.Ok(
                    $"Successfully uninstalled {packageId} from '{Path.GetFileName(csprojPath)}'.\n" +
                    $"A project file change was detected. Run 'build' to restore packages and rebuild the project."));
            }
            else
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"Package '{packageId}' was not found in '{Path.GetFileName(csprojPath)}'. " +
                    "It may already be uninstalled or the package ID is incorrect."));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Failed to uninstall {packageId}: {ex.Message}"));
        }
    }
}
