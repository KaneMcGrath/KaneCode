using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that searches NuGet.org for packages matching a query.
/// Returns package ID, version, description, authors, and download counts.
/// </summary>
internal sealed class NuGetSearchTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "query": {
                    "type": "string",
                    "description": "The search term to look for on NuGet.org."
                },
                "includePrerelease": {
                    "type": "boolean",
                    "description": "Whether to include prerelease packages in results. Defaults to false."
                },
                "take": {
                    "type": "integer",
                    "description": "Maximum number of results to return (1-30). Defaults to 10."
                }
            },
            "required": ["query"]
        }
        """).RootElement.Clone();

    private readonly NuGetService _nuGetService;

    public NuGetSearchTool(NuGetService nuGetService)
    {
        ArgumentNullException.ThrowIfNull(nuGetService);
        _nuGetService = nuGetService;
    }

    public string Name => "nuget_search";

    public string Category => "NuGet";

    public string Description =>
        "Searches NuGet.org for packages matching the given query. " +
        "Returns package ID, version, description, authors, and download counts. " +
        "Use this to find available NuGet packages before installing them.";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
        {
            return ToolCallResult.Fail("Missing required parameter: 'query'.");
        }

        var query = queryElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolCallResult.Fail("Search query cannot be empty.");
        }

        bool includePrerelease = false;
        if (arguments.TryGetProperty("includePrerelease", out var prereleaseElement) &&
            prereleaseElement.ValueKind == JsonValueKind.True)
        {
            includePrerelease = true;
        }

        int take = 10;
        if (arguments.TryGetProperty("take", out var takeElement) &&
            takeElement.ValueKind == JsonValueKind.Number)
        {
            take = Math.Clamp(takeElement.GetInt32(), 1, 30);
        }

        try
        {
            var results = await _nuGetService.SearchPackagesAsync(
                query,
                includePrerelease,
                take: take,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (results.Count == 0)
            {
                return ToolCallResult.Ok($"No packages found for '{query}'.");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} package(s) for '{query}':");
            sb.AppendLine();

            foreach (var pkg in results)
            {
                sb.AppendLine($"• {pkg.Id} v{pkg.VersionText}");
                if (!string.IsNullOrWhiteSpace(pkg.Description))
                {
                    var desc = pkg.Description.Length > 200
                        ? pkg.Description[..200] + "..."
                        : pkg.Description;
                    sb.AppendLine($"  {desc}");
                }
                if (!string.IsNullOrWhiteSpace(pkg.Authors))
                {
                    sb.AppendLine($"  Authors: {pkg.Authors}");
                }
                if (pkg.TotalDownloads > 0)
                {
                    sb.AppendLine($"  Downloads: {pkg.TotalDownloads:N0}");
                }
                if (pkg.IsPrerelease)
                {
                    sb.AppendLine("  (prerelease)");
                }
                sb.AppendLine();
            }

            return ToolCallResult.Ok(sb.ToString().TrimEnd());
        }
        catch (HttpRequestException ex)
        {
            return ToolCallResult.Fail($"Network error searching NuGet.org: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return ToolCallResult.Fail("Search timed out. Please try again.");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail($"Search failed: {ex.Message}");
        }
    }
}
