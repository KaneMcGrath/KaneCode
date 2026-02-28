using System.Diagnostics;
using System.IO;

namespace KaneCode.Services;

/// <summary>
/// Wraps the <c>dotnet</c> CLI to discover installed templates and scaffold new projects.
/// </summary>
internal sealed class DotnetCliService
{
    /// <summary>
    /// Environment variables set by MSBuildLocator that must be removed from child
    /// processes so that <c>dotnet</c> resolves its own SDK.
    /// </summary>
    private static readonly string[] s_msBuildEnvironmentVariables =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath"
    ];

    /// <summary>
    /// Discovers installed project templates by running <c>dotnet new list --type project --columns shortName,type,language</c>
    /// and parsing the tabular output.
    /// </summary>
    internal async Task<IReadOnlyList<DotnetTemplate>> GetProjectTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunDotnetAsync(
            "new list --type project --columns language",
            Directory.GetCurrentDirectory(),
            cancellationToken).ConfigureAwait(false);

        return ParseTemplateListOutput(output);
    }

    /// <summary>
    /// Creates a new project by running <c>dotnet new {shortName} --name {name} --output {outputPath}</c>.
    /// If provided, <paramref name="targetFramework"/> is passed via <c>--framework</c>.
    /// Returns the CLI output for diagnostics.
    /// </summary>
    internal async Task<string> CreateProjectAsync(
        string templateShortName,
        string projectName,
        string outputDirectory,
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(templateShortName))
        {
            throw new ArgumentException("Template short name is required.", nameof(templateShortName));
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException("Project name is required.", nameof(projectName));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        }

        var arguments = $"new {templateShortName} --name \"{projectName}\" --output \"{outputDirectory}\"";
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            arguments += $" --framework \"{targetFramework}\"";
        }

        // --output controls where files are created; the working directory just needs to exist
        var workingDirectory = FindExistingAncestor(outputDirectory);
        return await RunDotnetAsync(arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a solution file via <c>dotnet new sln</c> in the given directory,
    /// adds the discovered <c>.csproj</c> to it, and returns the created solution path.
    /// </summary>
    internal async Task<string> CreateSolutionAsync(
        string solutionName,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
        {
            throw new ArgumentException("Solution name is required.", nameof(solutionName));
        }

        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        }

        // dotnet new sln with working directory — matches: cd projectDir && dotnet new sln
        await RunDotnetAsync(
            $"new sln --name \"{solutionName}\"",
            projectDirectory,
            cancellationToken).ConfigureAwait(false);

        var solutionPath = ResolveCreatedSolutionPath(projectDirectory, solutionName);

        // Find the .csproj in the same directory and add it to the solution
        var csprojFile = Directory.EnumerateFiles(projectDirectory, "*.csproj").FirstOrDefault();
        if (string.IsNullOrEmpty(csprojFile))
        {
            return solutionPath;
        }

        await RunDotnetAsync(
            $"sln \"{solutionPath}\" add \"{csprojFile}\"",
            projectDirectory,
            cancellationToken).ConfigureAwait(false);

        return solutionPath;
    }

    private static string ResolveCreatedSolutionPath(string projectDirectory, string solutionName)
    {
        var slnPath = Path.Combine(projectDirectory, solutionName + ".sln");
        if (File.Exists(slnPath))
        {
            return slnPath;
        }

        var slnxPath = Path.Combine(projectDirectory, solutionName + ".slnx");
        if (File.Exists(slnxPath))
        {
            return slnxPath;
        }

        throw new InvalidOperationException(
            $"Solution file was not created in '{projectDirectory}'. Expected '{solutionName}.sln' or '{solutionName}.slnx'.");
    }

    /// <summary>
    /// Walks up the directory tree to find the nearest ancestor that exists on disk.
    /// Falls back to the current directory if none is found.
    /// </summary>
    private static string FindExistingAncestor(string path)
    {
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(directory))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static async Task<string> RunDotnetAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var key in s_msBuildEnvironmentVariables)
        {
            startInfo.Environment.Remove(key);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"dotnet {arguments} failed (exit code {process.ExitCode}):\n{message.Trim()}");
        }

        return stdout;
    }

    /// <summary>
    /// Parses the tabular output of <c>dotnet new list --type project</c>.
    /// The output has a header row followed by a separator (dashes), then data rows.
    /// </summary>
    private static List<DotnetTemplate> ParseTemplateListOutput(string output)
    {
        var templates = new List<DotnetTemplate>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // Find the separator line (all dashes and spaces) to locate column boundaries
        var separatorIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0 && trimmed.Replace("-", "").Replace(" ", "").Length == 0)
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex < 1 || separatorIndex + 1 >= lines.Length)
        {
            return templates;
        }

        // Parse column positions from the separator line
        var separator = lines[separatorIndex];
        var columnBounds = ParseColumnBounds(separator);

        if (columnBounds.Count < 2)
        {
            return templates;
        }

        // Data rows follow the separator
        for (var i = separatorIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var templateName = ExtractColumn(line, columnBounds, 0).Trim();
            var shortName = ExtractColumn(line, columnBounds, 1).Trim();
            var language = columnBounds.Count > 2
                ? ExtractColumn(line, columnBounds, 2).Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(templateName) || string.IsNullOrWhiteSpace(shortName))
            {
                continue;
            }

            // Only include C# templates (or language-agnostic ones)
            if (!string.IsNullOrEmpty(language)
                && !language.Contains("C#", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Short name can contain comma-separated aliases; take the first
            var primaryShortName = shortName.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

            templates.Add(new DotnetTemplate(templateName, primaryShortName, language));
        }

        return templates;
    }

    /// <summary>
    /// Given a separator line like "--------  --------  ------", returns the start positions
    /// of each dash-run segment.
    /// </summary>
    private static List<(int Start, int End)> ParseColumnBounds(string separator)
    {
        var bounds = new List<(int Start, int End)>();
        var i = 0;

        while (i < separator.Length)
        {
            // Skip whitespace
            while (i < separator.Length && separator[i] == ' ')
            {
                i++;
            }

            if (i >= separator.Length)
            {
                break;
            }

            var start = i;

            // Consume dash run
            while (i < separator.Length && separator[i] == '-')
            {
                i++;
            }

            if (i > start)
            {
                bounds.Add((start, i));
            }
        }

        return bounds;
    }

    private static string ExtractColumn(string line, List<(int Start, int End)> bounds, int columnIndex)
    {
        if (columnIndex >= bounds.Count)
        {
            return string.Empty;
        }

        var start = bounds[columnIndex].Start;
        var end = columnIndex + 1 < bounds.Count
            ? bounds[columnIndex + 1].Start
            : line.Length;

        if (start >= line.Length)
        {
            return string.Empty;
        }

        end = Math.Min(end, line.Length);
        return line[start..end];
    }
}

/// <summary>
/// Represents a dotnet SDK template discovered via <c>dotnet new list</c>.
/// </summary>
internal sealed record DotnetTemplate(string Name, string ShortName, string Language)
{
    /// <summary>Display string for combo boxes: "Console App (console)".</summary>
    public override string ToString() => $"{Name} ({ShortName})";
}
