using System.IO;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that lists all NuGet packages currently installed in the loaded project.
/// Reads PackageReference entries from the .csproj file.
/// </summary>
internal sealed class NuGetListInstalledTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectPathProvider;

    public NuGetListInstalledTool(Func<string?> projectPathProvider)
    {
        ArgumentNullException.ThrowIfNull(projectPathProvider);
        _projectPathProvider = projectPathProvider;
    }

    public string Name => "nuget_list_installed";

    public string Category => "NuGet";

    public string Description =>
        "Lists all NuGet packages currently installed in the loaded project by reading " +
        "PackageReference entries from the .csproj file. Returns package IDs and versions. " +
        "Use this before installing or uninstalling to see what's already there.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var projectPath = _projectPathProvider();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return Task.FromResult(ToolCallResult.Fail("No project or solution is currently loaded. Open a project first."));
        }

        // Collect all .csproj files we can find
        var csprojPaths = new List<string>();

        if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            csprojPaths.Add(projectPath);
        }
        else if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                 projectPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var solutionDir = Path.GetDirectoryName(projectPath);
            if (solutionDir is not null && Directory.Exists(solutionDir))
            {
                csprojPaths.AddRange(
                    Directory.EnumerateFiles(solutionDir, "*.csproj", SearchOption.AllDirectories));
            }
        }
        else if (Directory.Exists(projectPath))
        {
            csprojPaths.AddRange(
                Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories));
        }

        if (csprojPaths.Count == 0)
        {
            return Task.FromResult(ToolCallResult.Fail(
                "No .csproj files found. Open a .NET project or solution first."));
        }

        var sb = new StringBuilder();
        int totalPackages = 0;

        foreach (var csprojPath in csprojPaths)
        {
            try
            {
                var packages = NuGetService.GetInstalledPackages(csprojPath);

                if (packages.Count > 0)
                {
                    sb.AppendLine($"Project: {Path.GetFileName(csprojPath)}");
                    foreach (var pkg in packages.OrderBy(p => p.Id))
                    {
                        sb.AppendLine($"  • {pkg.Id} v{pkg.Version.ToFullString()}");
                    }
                    totalPackages += packages.Count;
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Project: {Path.GetFileName(csprojPath)} — error reading packages: {ex.Message}");
                sb.AppendLine();
            }
        }

        if (totalPackages == 0)
        {
            return Task.FromResult(ToolCallResult.Ok(
                "No NuGet packages are currently installed in this project."));
        }

        return Task.FromResult(ToolCallResult.Ok(
            $"{totalPackages} NuGet package(s) installed across {csprojPaths.Count} project(s):\n\n{sb.ToString().TrimEnd()}"));
    }
}
