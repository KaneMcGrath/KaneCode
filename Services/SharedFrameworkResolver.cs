using Microsoft.CodeAnalysis;
using System.IO;

namespace KaneCode.Services;

/// <summary>
/// Resolves metadata references from the installed .NET shared framework directory
/// instead of using TRUSTED_PLATFORM_ASSEMBLIES (which includes IDE-specific assemblies).
/// </summary>
internal static class SharedFrameworkResolver
{
    /// <summary>
    /// Returns metadata references for the .NET shared framework matching the given target framework.
    /// Falls back to the latest installed runtime if no match is found, or to the current runtime
    /// directory as a last resort.
    /// </summary>
    /// <param name="targetFramework">
    /// Target framework moniker such as "net8.0" or "net8.0-windows". Pass null to use the latest installed version.
    /// </param>
    public static List<MetadataReference> GetFrameworkReferences(string? targetFramework)
    {
        var dotnetRoot = GetDotnetRootPath();
        var majorVersion = ParseMajorVersion(targetFramework);
        var references = new List<MetadataReference>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Core runtime assemblies (Microsoft.NETCore.App)
        var coreDir = FindSharedFrameworkDirectory(dotnetRoot, "Microsoft.NETCore.App", majorVersion);
        if (coreDir is not null)
        {
            LoadAssembliesFrom(coreDir, references, addedPaths);
        }

        // Windows Desktop assemblies (WPF/WinForms) when the TFM targets Windows
        if (IsWindowsTfm(targetFramework))
        {
            var desktopDir = FindSharedFrameworkDirectory(dotnetRoot, "Microsoft.WindowsDesktop.App", majorVersion);
            if (desktopDir is not null)
            {
                LoadAssembliesFrom(desktopDir, references, addedPaths);
            }
        }

        if (references.Count > 0)
        {
            return references;
        }

        // Last-resort fallback: load a minimal set from the current runtime directory.
        // This path only runs if the dotnet installation cannot be found at all.
        return LoadFallbackReferences();
    }

    /// <summary>
    /// Returns true if the target framework targets Windows (e.g., "net8.0-windows").
    /// </summary>
    private static bool IsWindowsTfm(string? targetFramework)
    {
        if (string.IsNullOrEmpty(targetFramework))
        {
            return false;
        }

        return targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the best matching shared framework directory for the given framework name and major version.
    /// </summary>
    private static string? FindSharedFrameworkDirectory(string? dotnetRoot, string frameworkName, int? majorVersion)
    {
        if (dotnetRoot is null)
        {
            return null;
        }

        var sharedDir = Path.Combine(dotnetRoot, "shared", frameworkName);
        if (!Directory.Exists(sharedDir))
        {
            return null;
        }

        var versionDirs = GetVersionDirectories(sharedDir);

        if (versionDirs.Count == 0)
        {
            return null;
        }

        // If we have a major version, find the highest patch for that major
        if (majorVersion is not null)
        {
            var match = versionDirs
                .Where(v => v.Major == majorVersion.Value)
                .OrderByDescending(v => v.Version)
                .FirstOrDefault();

            if (match.Path is not null)
            {
                return match.Path;
            }
        }

        // Fall back to the highest installed version
        return versionDirs
            .OrderByDescending(v => v.Version)
            .First()
            .Path;
    }

    /// <summary>
    /// Extracts the major version number from a target framework string like "net8.0" or "net8.0-windows".
    /// </summary>
    private static int? ParseMajorVersion(string? targetFramework)
    {
        if (string.IsNullOrEmpty(targetFramework))
        {
            return null;
        }

        // Strip "net" prefix and any platform suffix (e.g., "net8.0-windows" -> "8.0")
        var tfm = targetFramework;
        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            tfm = tfm[3..];
        }

        // Remove platform suffix
        var dashIndex = tfm.IndexOf('-');
        if (dashIndex >= 0)
        {
            tfm = tfm[..dashIndex];
        }

        // Parse major version from "8.0" or "8"
        var dotIndex = tfm.IndexOf('.');
        var majorStr = dotIndex >= 0 ? tfm[..dotIndex] : tfm;

        return int.TryParse(majorStr, out var major) ? major : null;
    }

    /// <summary>
    /// Enumerates version directories under the shared framework path and parses their version numbers.
    /// </summary>
    private static List<(string Path, Version Version, int Major)> GetVersionDirectories(string sharedDir)
    {
        var results = new List<(string Path, Version Version, int Major)>();

        foreach (var dir in Directory.EnumerateDirectories(sharedDir))
        {
            var dirName = System.IO.Path.GetFileName(dir);
            if (Version.TryParse(dirName, out var version))
            {
                results.Add((dir, version, version.Major));
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the dotnet installation root directory.
    /// </summary>
    private static string? GetDotnetRootPath()
    {
        // Check DOTNET_ROOT environment variable first
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        // Derive from the runtime directory of the current process
        // typeof(object).Assembly.Location is inside the shared framework dir,
        // e.g., C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.x\System.Private.CoreLib.dll
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir is not null)
        {
            // Walk up: 8.0.x -> Microsoft.NETCore.App -> shared -> dotnet root
            var candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            if (Directory.Exists(Path.Combine(candidate, "shared", "Microsoft.NETCore.App")))
            {
                return candidate;
            }
        }

        // Well-known paths
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            if (Directory.Exists(programFiles))
            {
                return programFiles;
            }
        }

        return null;
    }

    private static void LoadAssembliesFrom(string directory, List<MetadataReference> references, HashSet<string> addedPaths)
    {
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
        {
            if (!addedPaths.Add(dll))
            {
                continue;
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(dll));
            }
            catch
            {
                // Skip assemblies that can't be loaded (e.g., native shims)
            }
        }
    }

    private static List<MetadataReference> LoadFallbackReferences()
    {
        var references = new List<MetadataReference>();
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir is null)
        {
            return references;
        }

        string[] coreAssemblies =
        [
            "System.Runtime.dll",
            "System.Console.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Threading.dll",
            "System.Threading.Tasks.dll",
            "System.Text.RegularExpressions.dll",
            "System.IO.dll",
            "System.IO.FileSystem.dll",
            "System.Net.Http.dll",
            "mscorlib.dll",
            "netstandard.dll",
            "System.Private.CoreLib.dll"
        ];

        foreach (var assembly in coreAssemblies)
        {
            var path = Path.Combine(runtimeDir, assembly);
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return references;
    }
}
