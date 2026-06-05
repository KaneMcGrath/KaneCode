using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that gets detailed metadata about a specific NuGet package from NuGet.org.
/// Returns version info, description, authors, project URL, license URL, and tags.
/// </summary>
internal sealed class NuGetInfoTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "packageId": {
                    "type": "string",
                    "description": "The NuGet package ID to look up (e.g. 'Newtonsoft.Json')."
                },
                "version": {
                    "type": "string",
                    "description": "Optional specific version to look up. If omitted, the latest stable version is returned."
                }
            },
            "required": ["packageId"]
        }
        """).RootElement.Clone();

    private readonly NuGetService _nuGetService;

    public NuGetInfoTool(NuGetService nuGetService)
    {
        ArgumentNullException.ThrowIfNull(nuGetService);
        _nuGetService = nuGetService;
    }

    public string Name => "nuget_info";

    public string Category => "NuGet";

    public string Description =>
        "Gets detailed metadata about a specific NuGet package from NuGet.org. " +
        "Returns version info, description, authors, project URL, license URL, tags, and download count. " +
        "Use this to inspect a package before installing it.";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("packageId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            return ToolCallResult.Fail("Missing required parameter: 'packageId'.");
        }

        var packageId = idElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return ToolCallResult.Fail("Package ID cannot be empty.");
        }

        NuGet.Versioning.NuGetVersion? version = null;
        if (arguments.TryGetProperty("version", out var versionElement) &&
            versionElement.ValueKind == JsonValueKind.String)
        {
            var versionStr = versionElement.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(versionStr))
            {
                if (!NuGet.Versioning.NuGetVersion.TryParse(versionStr, out version))
                {
                    return ToolCallResult.Fail($"Invalid version format: '{versionStr}'. Expected a valid NuGet version like '13.0.3'.");
                }
            }
        }

        try
        {
            var detail = await _nuGetService.GetPackageDetailAsync(
                packageId, version, cancellationToken).ConfigureAwait(false);

            if (detail is null)
            {
                return ToolCallResult.Fail($"Package '{packageId}' was not found on NuGet.org.");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Package: {detail.Id}");
            sb.AppendLine($"Version: {detail.VersionText}");
            sb.AppendLine($"Title: {detail.Title}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(detail.Description))
            {
                sb.AppendLine("Description:");
                sb.AppendLine(detail.Description);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(detail.Authors))
            {
                sb.AppendLine($"Authors: {detail.Authors}");
            }

            if (detail.TotalDownloads > 0)
            {
                sb.AppendLine($"Total Downloads: {detail.TotalDownloads:N0}");
            }

            if (detail.ProjectUrl is not null)
            {
                sb.AppendLine($"Project URL: {detail.ProjectUrl}");
            }

            if (detail.LicenseUrl is not null)
            {
                sb.AppendLine($"License URL: {detail.LicenseUrl}");
            }

            if (!string.IsNullOrWhiteSpace(detail.Tags))
            {
                sb.AppendLine($"Tags: {detail.Tags}");
            }

            if (detail.IsPrerelease)
            {
                sb.AppendLine("(prerelease)");
            }

            return ToolCallResult.Ok(sb.ToString().TrimEnd());
        }
        catch (HttpRequestException ex)
        {
            return ToolCallResult.Fail($"Network error looking up package '{packageId}': {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return ToolCallResult.Fail($"Request timed out looking up package '{packageId}'.");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail($"Failed to look up package '{packageId}': {ex.Message}");
        }
    }
}
