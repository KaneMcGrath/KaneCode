using System.IO;
using System.Linq;

namespace KaneCode.Services.Ai;

/// <summary>
/// Stores request-scoped external directories that agent file tools may access.
/// </summary>
internal sealed class ExternalContextDirectoryRegistry
{
    private readonly object _syncLock = new();
    private IReadOnlyList<string> _allowedDirectories = [];

    internal void SetAllowedDirectories(IReadOnlyList<string> directoryPaths)
    {
        ArgumentNullException.ThrowIfNull(directoryPaths);

        List<string> normalizedPaths = [];

        foreach (string directoryPath in directoryPaths)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            string normalizedPath = Path.GetFullPath(directoryPath);
            if (normalizedPaths.Any(existingPath => string.Equals(existingPath, normalizedPath, GetPathComparison())))
            {
                continue;
            }

            normalizedPaths.Add(normalizedPath);
        }

        lock (_syncLock)
        {
            _allowedDirectories = normalizedPaths;
        }
    }

    internal IReadOnlyList<string> GetAllowedDirectories()
    {
        lock (_syncLock)
        {
            return [.. _allowedDirectories];
        }
    }

    internal void Clear()
    {
        lock (_syncLock)
        {
            _allowedDirectories = [];
        }
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
