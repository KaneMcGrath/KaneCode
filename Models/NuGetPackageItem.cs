using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace KaneCode.Models;

/// <summary>
/// Represents a NuGet package displayed in the package manager UI.
/// </summary>
internal sealed class NuGetPackageItem
{
    /// <summary>The package identity (id + version).</summary>
    public PackageIdentity Identity { get; }

    /// <summary>The package ID.</summary>
    public string Id => Identity.Id;

    /// <summary>The package version.</summary>
    public NuGetVersion Version => Identity.Version;

    /// <summary>Human-readable version string.</summary>
    public string VersionText => Version.ToFullString();

    /// <summary>Title for display (falls back to Id).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Description of the package.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Authors string.</summary>
    public string Authors { get; set; } = string.Empty;

    /// <summary>Project URL.</summary>
    public Uri? ProjectUrl { get; set; }

    /// <summary>License URL.</summary>
    public Uri? LicenseUrl { get; set; }

    /// <summary>Icon URL.</summary>
    public Uri? IconUrl { get; set; }

    /// <summary>Tags string.</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>Total download count.</summary>
    public long TotalDownloads { get; set; }

    /// <summary>Whether this package is a prerelease version.</summary>
    public bool IsPrerelease { get; set; }

    /// <summary>
    /// Whether the package is currently installed in the selected project.
    /// </summary>
    public bool IsInstalled { get; set; }

    /// <summary>
    /// The installed version if this package is already in the project.
    /// </summary>
    public NuGetVersion? InstalledVersion { get; set; }

    /// <summary>
    /// Whether a newer version than installed is available.
    /// </summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// Display text for the list view combining title/ID, version, and downloads.
    /// </summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Title)
        ? $"{Id} {VersionText}"
        : $"{Title} ({Id}) {VersionText}";

    /// <summary>Detail text showing download count.</summary>
    public string DownloadCountText => TotalDownloads > 0
        ? $"{TotalDownloads:N0} downloads"
        : string.Empty;

    /// <summary>Short summary for the list.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { VersionText };
            if (!string.IsNullOrWhiteSpace(DownloadCountText))
            {
                parts.Add(DownloadCountText);
            }
            if (IsPrerelease)
            {
                parts.Add("(prerelease)");
            }
            return string.Join(" • ", parts);
        }
    }

    public NuGetPackageItem(PackageIdentity identity)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }
}
