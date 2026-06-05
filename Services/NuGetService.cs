using KaneCode.Models;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KaneCode.Services;

/// <summary>
/// Service for managing NuGet packages: searching nuget.org, querying package details,
/// and installing/updating/uninstalling packages via .csproj file manipulation.
/// </summary>
internal sealed class NuGetService : IDisposable
{
    private readonly SourceRepository _nuGetRepository;
    private readonly SourceCacheContext _cacheContext;
    private readonly ILogger _logger;

    private PackageSearchResource? _searchResource;
    private PackageMetadataResource? _metadataResource;
    private FindPackageByIdResource? _findByIdResource;

    /// <summary>
    /// Default NuGet.org feed URL.
    /// </summary>
    internal const string NuGetOrgFeed = "https://api.nuget.org/v3/index.json";

    public NuGetService()
    {
        _logger = NullLogger.Instance;
        _cacheContext = new SourceCacheContext();

        var providers = Repository.Provider.GetCoreV3();
        _nuGetRepository = new SourceRepository(new PackageSource(NuGetOrgFeed), providers);
    }

    /// <summary>
    /// Searches NuGet.org for packages matching the query.
    /// </summary>
    /// <param name="query">Search text.</param>
    /// <param name="includePrerelease">Whether to include prerelease packages.</param>
    /// <param name="skip">Number of results to skip (for pagination).</param>
    /// <param name="take">Number of results to take (max 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of NuGet package items matching the query.</returns>
    public async Task<IReadOnlyList<NuGetPackageItem>> SearchPackagesAsync(
        string query,
        bool includePrerelease = false,
        int skip = 0,
        int take = 30,
        CancellationToken cancellationToken = default)
    {
        _searchResource ??= await _nuGetRepository.GetResourceAsync<PackageSearchResource>(cancellationToken)
            .ConfigureAwait(false);

        var searchFilter = new SearchFilter(includePrerelease)
        {
            IncludeDelisted = false
        };

        var results = await _searchResource.SearchAsync(
            query,
            searchFilter,
            skip,
            take,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var items = new List<NuGetPackageItem>();

        foreach (var result in results)
        {
            var identity = result.Identity;
            var item = new NuGetPackageItem(identity)
            {
                Title = result.Title ?? identity.Id,
                Description = result.Description ?? string.Empty,
                Authors = string.Join(", ", result.Authors),
                ProjectUrl = result.ProjectUrl,
                LicenseUrl = result.LicenseUrl,
                IconUrl = result.IconUrl,
                Tags = result.Tags ?? string.Empty,
                TotalDownloads = result.DownloadCount ?? 0,
                IsPrerelease = identity.Version.IsPrerelease
            };

            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Gets all available versions for a package from NuGet.org.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="includePrerelease">Whether to include prerelease versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of versions, sorted descending (latest first).</returns>
    public async Task<IReadOnlyList<NuGetVersion>> GetPackageVersionsAsync(
        string packageId,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        _findByIdResource ??= await _nuGetRepository.GetResourceAsync<FindPackageByIdResource>(cancellationToken)
            .ConfigureAwait(false);

        var versions = await _findByIdResource.GetAllVersionsAsync(
            packageId,
            _cacheContext,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var filtered = includePrerelease
            ? versions
            : versions.Where(v => !v.IsPrerelease);

        return filtered
            .OrderByDescending(v => v)
            .ToList();
    }

    /// <summary>
    /// Gets detailed metadata for a specific package version.
    /// </summary>
    public async Task<NuGetPackageItem?> GetPackageDetailAsync(
        string packageId,
        NuGetVersion? version = null,
        CancellationToken cancellationToken = default)
    {
        _metadataResource ??= await _nuGetRepository.GetResourceAsync<PackageMetadataResource>(cancellationToken)
            .ConfigureAwait(false);

        var metadata = await _metadataResource.GetMetadataAsync(
            packageId,
            includePrerelease: true,
            includeUnlisted: false,
            _cacheContext,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var package = metadata?.FirstOrDefault();
        if (package is null)
        {
            return null;
        }

        var identity = new PackageIdentity(packageId, version ?? package.Identity.Version);
        return new NuGetPackageItem(identity)
        {
            Title = package.Title ?? packageId,
            Description = package.Description ?? string.Empty,
            Authors = string.Join(", ", package.Authors),
            ProjectUrl = package.ProjectUrl,
            LicenseUrl = package.LicenseUrl,
            IconUrl = package.IconUrl,
            Tags = package.Tags ?? string.Empty,
            TotalDownloads = package.DownloadCount ?? 0,
            IsPrerelease = identity.Version.IsPrerelease
        };
    }

    /// <summary>
    /// Reads all installed package references from a .csproj file.
    /// </summary>
    /// <param name="projectPath">Full path to the .csproj file.</param>
    /// <returns>List of installed package identities.</returns>
    public static IReadOnlyList<PackageIdentity> GetInstalledPackages(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return [];
        }

        var doc = XDocument.Load(projectPath);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var packages = new List<PackageIdentity>();

        var packageRefs = doc.Descendants(ns + "PackageReference");
        foreach (var pr in packageRefs)
        {
            var id = pr.Attribute("Include")?.Value;
            var version = pr.Attribute("Version")?.Value;

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
            {
                if (NuGetVersion.TryParse(version, out var nuGetVersion))
                {
                    packages.Add(new PackageIdentity(id, nuGetVersion));
                }
            }
        }

        return packages;
    }

    /// <summary>
    /// Installs a NuGet package into the specified .csproj file by adding a PackageReference.
    /// </summary>
    /// <param name="projectPath">Full path to the .csproj file.</param>
    /// <param name="packageId">The package ID to install.</param>
    /// <param name="version">The version to install.</param>
    /// <returns>True if the package was added, false if it already exists.</returns>
    public static bool InstallPackage(string projectPath, string packageId, NuGetVersion version)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new FileNotFoundException("Project file not found.", projectPath);
        }

        var doc = XDocument.Load(projectPath);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        // Check if already installed
        var existing = doc.Descendants(ns + "PackageReference")
            .FirstOrDefault(pr => string.Equals(
                pr.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return false; // Already installed
        }

        // Find or create the right item group
        var itemGroup = doc.Descendants(ns + "ItemGroup")
            .FirstOrDefault(ig => ig.Descendants(ns + "PackageReference").Any());

        if (itemGroup is null)
        {
            // No existing ItemGroup with PackageReference; find any ItemGroup
            itemGroup = doc.Descendants(ns + "ItemGroup").FirstOrDefault();
        }

        if (itemGroup is null)
        {
            // No ItemGroup at all; create one after the PropertyGroup
            var propertyGroup = doc.Descendants(ns + "PropertyGroup").FirstOrDefault();
            if (propertyGroup is null)
            {
                // Fallback: add after root
                doc.Root?.Add(new XElement(ns + "ItemGroup"));
            }
            else
            {
                propertyGroup.AddAfterSelf(new XElement(ns + "ItemGroup"));
            }

            itemGroup = doc.Descendants(ns + "ItemGroup").Last();
        }

        var packageRef = new XElement(ns + "PackageReference",
            new XAttribute("Include", packageId),
            new XAttribute("Version", version.ToFullString()));

        itemGroup.Add(packageRef);
        doc.Save(projectPath);
        return true;
    }

    /// <summary>
    /// Updates a package to the specified version (or uninstalls if version is null).
    /// </summary>
    public static bool UpdatePackage(string projectPath, string packageId, NuGetVersion newVersion)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new FileNotFoundException("Project file not found.", projectPath);
        }

        var doc = XDocument.Load(projectPath);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var existing = doc.Descendants(ns + "PackageReference")
            .FirstOrDefault(pr => string.Equals(
                pr.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            return false;
        }

        existing.SetAttributeValue("Version", newVersion.ToFullString());
        doc.Save(projectPath);
        return true;
    }

    /// <summary>
    /// Uninstalls a package by removing its PackageReference from the .csproj file.
    /// </summary>
    /// <param name="projectPath">Full path to the .csproj file.</param>
    /// <param name="packageId">The package ID to remove.</param>
    /// <returns>True if the package was removed, false if not found.</returns>
    public static bool UninstallPackage(string projectPath, string packageId)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new FileNotFoundException("Project file not found.", projectPath);
        }

        var doc = XDocument.Load(projectPath);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var existing = doc.Descendants(ns + "PackageReference")
            .FirstOrDefault(pr => string.Equals(
                pr.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            return false;
        }

        existing.Remove();
        doc.Save(projectPath);
        return true;
    }

    /// <summary>
    /// Returns all .csproj file paths from the solution's loaded project paths.
    /// </summary>
    /// <param name="solutionProjectPaths">The list of project paths from a loaded solution.</param>
    /// <param name="singleProjectPath">The single .csproj path when no solution is loaded.</param>
    /// <returns>List of .csproj file paths.</returns>
    public static IReadOnlyList<string> GetProjectPaths(
        IReadOnlyList<string>? solutionProjectPaths,
        string? singleProjectPath)
    {
        if (solutionProjectPaths is not null && solutionProjectPaths.Count > 0)
        {
            return solutionProjectPaths;
        }

        if (!string.IsNullOrWhiteSpace(singleProjectPath) &&
            singleProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return [singleProjectPath];
        }

        return [];
    }

    public void Dispose()
    {
        _cacheContext.Dispose();
    }
}
