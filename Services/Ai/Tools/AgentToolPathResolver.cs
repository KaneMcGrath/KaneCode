using System.IO;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Resolves agent tool paths against the loaded project root and enforces sandboxing.
/// </summary>
internal static class AgentToolPathResolver
{
    internal static string GetProjectRootDirectory(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);

        string? projectRoot = projectRootProvider();
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new InvalidOperationException("No project or solution is currently loaded.");
        }

        string fullProjectRoot = Path.GetFullPath(projectRoot);

        if (File.Exists(fullProjectRoot))
        {
            string? directory = Path.GetDirectoryName(fullProjectRoot);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The loaded project root could not be resolved.");
            }

            return directory;
        }

        if (Directory.Exists(fullProjectRoot))
        {
            return fullProjectRoot;
        }

        throw new InvalidOperationException("The loaded project root could not be resolved.");
    }

    internal static string ResolvePath(
        Func<string?> projectRootProvider,
        string inputPath,
        IReadOnlyList<string>? allowedExternalRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        string projectRoot = GetProjectRootDirectory(projectRootProvider);

        // When the input path is relative and its first segment matches the project root's
        // leaf directory name (e.g. input is "KaneCode/Models/File.cs" and projectRoot is
        // "...\KaneCode\KaneCode"), combining them would produce a duplicate folder segment.
        // In that case, resolve from the project root's parent directory instead.
        if (!Path.IsPathRooted(inputPath))
        {
            string projectDirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRoot));

            if (!string.IsNullOrEmpty(projectDirName) && StartsWithDirectory(inputPath, projectDirName))
            {
                string? parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(projectRoot));

                if (parentDir is not null)
                {
                    string parentCandidatePath = Path.GetFullPath(Path.Combine(parentDir, inputPath));

                    if (IsPathWithinRoot(parentCandidatePath, projectRoot))
                    {
                        return parentCandidatePath;
                    }
                }
            }
        }

        string candidatePath = Path.IsPathRooted(inputPath)
            ? Path.GetFullPath(inputPath)
            : Path.GetFullPath(Path.Combine(projectRoot, inputPath));

        if (!IsPathWithinRoot(candidatePath, projectRoot) && !IsPathWithinAnyRoot(candidatePath, allowedExternalRoots))
        {
            throw new InvalidOperationException($"Path must stay inside the loaded project or an attached external context folder: {inputPath}");
        }

        return candidatePath;
    }

    internal static string ResolveFilePathOrFileName(
        Func<string?> projectRootProvider,
        string fileInput,
        IReadOnlySet<string> skippedDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileInput);
        ArgumentNullException.ThrowIfNull(skippedDirectories);

        string projectRoot = GetProjectRootDirectory(projectRootProvider);

        if (Path.IsPathRooted(fileInput) || ContainsDirectorySeparator(fileInput))
        {
            return ResolvePath(projectRootProvider, fileInput);
        }

        string directPath = ResolvePath(projectRootProvider, fileInput);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories))
            {
                if (IsInSkippedDirectory(path, skippedDirectories))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(path), fileInput, GetPathComparison()))
                {
                    return path;
                }
            }
        }
        catch (IOException)
        {
            return directPath;
        }
        catch (UnauthorizedAccessException)
        {
            return directPath;
        }

        return directPath;
    }

    internal static bool IsInSkippedDirectory(string path, IReadOnlySet<string> skippedDirectories)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(skippedDirectories);

        string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string part in parts)
        {
            if (skippedDirectories.Contains(part))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDirectorySeparator(string path)
    {
        return path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> starts with <paramref name="directoryName"/>
    /// followed by a directory separator, or is exactly equal to <paramref name="directoryName"/>.
    /// </summary>
    private static bool StartsWithDirectory(string path, string directoryName)
    {
        StringComparison comparison = GetPathComparison();

        return string.Equals(path, directoryName, comparison)
            || path.StartsWith(directoryName + Path.DirectorySeparatorChar, comparison)
            || path.StartsWith(directoryName + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsPathWithinRoot(string candidatePath, string rootPath)
    {
        StringComparison comparison = GetPathComparison();
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string normalizedCandidate = Path.GetFullPath(candidatePath);

        return string.Equals(normalizedCandidate, normalizedRoot, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsPathWithinAnyRoot(string candidatePath, IReadOnlyList<string>? rootPaths)
    {
        if (rootPaths is null || rootPaths.Count == 0)
        {
            return false;
        }

        foreach (string rootPath in rootPaths)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue;
            }

            if (IsPathWithinRoot(candidatePath, rootPath))
            {
                return true;
            }
        }

        return false;
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
