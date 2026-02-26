using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using System.IO;

using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace KaneCode.Services;

/// <summary>
/// Loads .csproj and .sln files using MSBuild evaluation and populates
/// a <see cref="RoslynWorkspaceService"/> with the project structure,
/// source files, and references.
/// </summary>
internal sealed class MSBuildProjectLoader
{
    private static bool _msBuildRegistered;
    private static readonly object _registerLock = new();

    /// <summary>
    /// Ensures MSBuild is registered via MSBuildLocator. Must be called before any MSBuild API usage.
    /// </summary>
    public static void EnsureMSBuildRegistered()
    {
        if (_msBuildRegistered)
        {
            return;
        }

        lock (_registerLock)
        {
            if (_msBuildRegistered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                var instance = MSBuildLocator.QueryVisualStudioInstances()
                    .OrderByDescending(i => i.Version)
                    .FirstOrDefault();

                if (instance is not null)
                {
                    MSBuildLocator.RegisterInstance(instance);
                }
                else
                {
                    MSBuildLocator.RegisterDefaults();
                }
            }

            _msBuildRegistered = true;
        }
    }

    /// <summary>
    /// Loads a .sln file, discovers all C# projects, and loads them into the workspace.
    /// </summary>
    /// <returns>Information about the loaded solution.</returns>
    public static LoadedSolutionInfo LoadSolution(string solutionPath, RoslynWorkspaceService workspaceService)
    {
        ArgumentNullException.ThrowIfNull(solutionPath);
        ArgumentNullException.ThrowIfNull(workspaceService);

        EnsureMSBuildRegistered();

        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var projectPaths = ParseSolutionProjectPaths(solutionPath, solutionDir);
        var loadedProjects = new List<LoadedProjectInfo>();

        workspaceService.ClearWorkspace();

        foreach (var projectPath in projectPaths)
        {
            if (!File.Exists(projectPath))
            {
                continue;
            }

            var ext = Path.GetExtension(projectPath);
            if (!ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var projectInfo = LoadProjectInternal(projectPath, workspaceService);
                if (projectInfo is not null)
                {
                    loadedProjects.Add(projectInfo);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load project {projectPath}: {ex.Message}");
            }
        }

        // Clean up the global project collection to avoid stale state
        ProjectCollection.GlobalProjectCollection.UnloadAllProjects();

        return new LoadedSolutionInfo(
            solutionPath,
            Path.GetFileNameWithoutExtension(solutionPath),
            loadedProjects.ToImmutableArray());
    }

    /// <summary>
    /// Loads a single .csproj file into the workspace.
    /// </summary>
    public static LoadedProjectInfo? LoadProject(string projectPath, RoslynWorkspaceService workspaceService)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(workspaceService);

        EnsureMSBuildRegistered();

        workspaceService.ClearWorkspace();

        try
        {
            var info = LoadProjectInternal(projectPath, workspaceService);
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
            System.Diagnostics.Debug.WriteLine($"Failed to load project {projectPath}: {ex.Message}");
            return null;
        }
    }

    private static LoadedProjectInfo? LoadProjectInternal(string projectPath, RoslynWorkspaceService workspaceService)
    {
        var project = new MSBuildProject(projectPath, null, null, ProjectCollection.GlobalProjectCollection,
            ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);

        var projectDir = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        // Extract compilation settings
        var targetFramework = project.GetPropertyValue("TargetFramework");
        var outputType = project.GetPropertyValue("OutputType");
        var langVersion = project.GetPropertyValue("LangVersion");
        var nullableStr = project.GetPropertyValue("Nullable");
        var allowUnsafe = project.GetPropertyValue("AllowUnsafeBlocks");
        var rootNamespace = project.GetPropertyValue("RootNamespace");
        var assemblyName = project.GetPropertyValue("AssemblyName");

        if (string.IsNullOrEmpty(assemblyName))
        {
            assemblyName = projectName;
        }

        // Determine output kind
        var outputKind = outputType?.ToLowerInvariant() switch
        {
            "exe" => OutputKind.ConsoleApplication,
            "winexe" => OutputKind.WindowsApplication,
            "library" => OutputKind.DynamicallyLinkedLibrary,
            _ => OutputKind.DynamicallyLinkedLibrary
        };

        // Parse language version
        if (!LanguageVersionFacts.TryParse(langVersion, out var languageVersion))
        {
            languageVersion = LanguageVersion.Latest;
        }

        // Nullable context
        var nullableContextOptions = nullableStr?.ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            "disable" => NullableContextOptions.Disable,
            _ => NullableContextOptions.Disable
        };

        var compilationOptions = new CSharpCompilationOptions(outputKind)
            .WithNullableContextOptions(nullableContextOptions)
            .WithAllowUnsafe(string.Equals(allowUnsafe, "true", StringComparison.OrdinalIgnoreCase));

        var parseOptions = new CSharpParseOptions(languageVersion);

        // Collect source files
        var sourceFiles = new List<string>();
        foreach (var item in project.GetItems("Compile"))
        {
            var filePath = Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude));
            if (File.Exists(filePath))
            {
                sourceFiles.Add(filePath);
            }
        }

        // Collect metadata references from resolved assemblies
        var metadataReferences = ResolveMetadataReferences(project, projectDir, targetFramework);

        // Collect preprocessor symbols
        var defineConstants = project.GetPropertyValue("DefineConstants");
        if (!string.IsNullOrEmpty(defineConstants))
        {
            var symbols = defineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            parseOptions = parseOptions.WithPreprocessorSymbols((IEnumerable<string>)symbols);
        }

        // Register the project in the workspace
        var projectId = workspaceService.AddProject(
            assemblyName,
            compilationOptions,
            parseOptions,
            metadataReferences);

        // Add source files
        foreach (var sourceFile in sourceFiles)
        {
            var text = File.ReadAllText(sourceFile);
            workspaceService.AddDocumentToProject(projectId, sourceFile, text);
        }

        return new LoadedProjectInfo(
            projectPath,
            projectName,
            targetFramework,
            projectId,
            sourceFiles.ToImmutableArray());
    }

    private static List<MetadataReference> ResolveMetadataReferences(
        MSBuildProject msbuildProject, string projectDir, string targetFramework)
    {
        var references = new List<MetadataReference>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try to resolve from PackageReference items by finding assemblies in the NuGet cache
        foreach (var item in msbuildProject.GetItems("PackageReference"))
        {
            var packageName = item.EvaluatedInclude;
            var version = item.GetMetadataValue("Version");
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(version))
            {
                continue;
            }

            ResolvePackageAssemblies(references, addedPaths, packageName, version, targetFramework);
        }

        // Add framework references from the runtime
        AddFrameworkReferences(references, addedPaths, targetFramework);

        return references;
    }

    private static void ResolvePackageAssemblies(
        List<MetadataReference> references,
        HashSet<string> addedPaths,
        string packageName,
        string version,
        string targetFramework)
    {
        var nugetCache = GetNuGetPackagesPath();
        if (nugetCache is null)
        {
            return;
        }

        var packageDir = Path.Combine(nugetCache, packageName.ToLowerInvariant(), version.ToLowerInvariant());
        if (!Directory.Exists(packageDir))
        {
            return;
        }

        var libDir = Path.Combine(packageDir, "lib");
        if (!Directory.Exists(libDir))
        {
            return;
        }

        // Find the best matching TFM directory
        var tfmDir = FindBestTfmDirectory(libDir, targetFramework);
        if (tfmDir is null)
        {
            return;
        }

        foreach (var dll in Directory.EnumerateFiles(tfmDir, "*.dll"))
        {
            if (addedPaths.Add(dll))
            {
                references.Add(MetadataReference.CreateFromFile(dll));
            }
        }
    }

    private static void AddFrameworkReferences(
        List<MetadataReference> references,
        HashSet<string> addedPaths,
        string targetFramework)
    {
        // Use trusted platform assemblies from the current runtime
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(trustedAssemblies))
        {
            return;
        }

        foreach (var assemblyPath in trustedAssemblies.Split(Path.PathSeparator))
        {
            if (File.Exists(assemblyPath) && addedPaths.Add(assemblyPath))
            {
                references.Add(MetadataReference.CreateFromFile(assemblyPath));
            }
        }
    }

    private static string? FindBestTfmDirectory(string libDir, string targetFramework)
    {
        if (!Directory.Exists(libDir))
        {
            return null;
        }

        var dirs = Directory.GetDirectories(libDir)
            .Select(d => (Path: d, Name: Path.GetFileName(d)))
            .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Exact match first
        var exact = dirs.FirstOrDefault(d =>
            d.Name.Equals(targetFramework, StringComparison.OrdinalIgnoreCase));
        if (exact.Path is not null)
        {
            return exact.Path;
        }

        // Try matching major version (e.g., net8.0 matches net8.0-windows)
        var majorMatch = dirs.FirstOrDefault(d =>
            targetFramework.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase) ||
            d.Name.StartsWith(targetFramework.Split('-')[0], StringComparison.OrdinalIgnoreCase));
        if (majorMatch.Path is not null)
        {
            return majorMatch.Path;
        }

        // Fall back to netstandard2.0, netstandard2.1, or the first available
        var netstandard = dirs.FirstOrDefault(d =>
            d.Name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase));
        if (netstandard.Path is not null)
        {
            return netstandard.Path;
        }

        return dirs.FirstOrDefault().Path;
    }

    private static string? GetNuGetPackagesPath()
    {
        var nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(nugetPackages) && Directory.Exists(nugetPackages))
        {
            return nugetPackages;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(userProfile, ".nuget", "packages");
        if (Directory.Exists(defaultPath))
        {
            return defaultPath;
        }

        return null;
    }

    /// <summary>
    /// Parses a .sln file to extract project file paths.
    /// </summary>
    private static List<string> ParseSolutionProjectPaths(string solutionPath, string solutionDir)
    {
        var projectPaths = new List<string>();
        var lines = File.ReadAllLines(solutionPath);

        foreach (var line in lines)
        {
            // Format: Project("{FAE04EC0-...}") = "ProjectName", "RelativePath\Project.csproj", "{GUID}"
            if (!line.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('"');
            // parts[0] = 'Project('
            // parts[1] = project type GUID
            // parts[2] = ') = '
            // parts[3] = project name
            // parts[4] = ', '
            // parts[5] = relative path
            if (parts.Length >= 6)
            {
                var relativePath = parts[5].Replace('\\', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
                projectPaths.Add(fullPath);
            }
        }

        return projectPaths;
    }
}

/// <summary>
/// Information about a loaded solution.
/// </summary>
internal sealed record LoadedSolutionInfo(
    string SolutionPath,
    string Name,
    ImmutableArray<LoadedProjectInfo> Projects);

/// <summary>
/// Information about a loaded project.
/// </summary>
internal sealed record LoadedProjectInfo(
    string ProjectPath,
    string Name,
    string TargetFramework,
    ProjectId RoslynProjectId,
    ImmutableArray<string> SourceFiles);
