using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;

using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace KaneCode.Services;

/// <summary>
/// Loads .csproj and .sln files using MSBuild evaluation and populates
/// a <see cref="RoslynWorkspaceService"/> with the project structure,
/// source files, and references.
/// </summary>
internal static class MSBuildProjectLoader
{
    private static bool _msBuildRegistered;
    private static readonly object _registerLock = new();
    private static readonly string[] s_generatedSourcePatterns = ["*.g.cs", "*.g.i.cs"];
    private static readonly SimpleAnalyzerAssemblyLoader s_analyzerLoader = SimpleAnalyzerAssemblyLoader.Instance;

    /// <summary>
    /// Minimal <see cref="IAnalyzerAssemblyLoader"/> implementation used to construct
    /// <see cref="AnalyzerFileReference"/> instances for the Roslyn workspace.
    /// </summary>
    private sealed class SimpleAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static readonly SimpleAnalyzerAssemblyLoader Instance = new();

        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

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
    /// Attempts high-fidelity loading via <c>MSBuildWorkspace</c> first, then falls back
    /// to manual MSBuild evaluation if that fails.
    /// </summary>
    /// <returns>Information about the loaded solution.</returns>
    public static async Task<LoadedSolutionInfo> LoadSolutionAsync(string solutionPath, RoslynWorkspaceService workspaceService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solutionPath);
        ArgumentNullException.ThrowIfNull(workspaceService);

        EnsureMSBuildRegistered();

        try
        {
            return await LoadSolutionWithMSBuildWorkspaceAsync(solutionPath, workspaceService, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"MSBuildWorkspace solution load failed, falling back to manual loading: {ex.Message}");
            return await LoadSolutionManuallyAsync(solutionPath, workspaceService, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads a solution using <c>MSBuildWorkspace</c> for full-fidelity project loading.
    /// </summary>
    private static async Task<LoadedSolutionInfo> LoadSolutionWithMSBuildWorkspaceAsync(
        string solutionPath, RoslynWorkspaceService workspaceService, CancellationToken cancellationToken)
    {
        MSBuildWorkspace msbuildWorkspace = MSBuildWorkspace.Create();
        try
        {
            Microsoft.CodeAnalysis.Solution solution = await msbuildWorkspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (WorkspaceDiagnostic diagnostic in msbuildWorkspace.Diagnostics)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"MSBuildWorkspace [{diagnostic.Kind}]: {diagnostic.Message}");
            }

            await workspaceService.ReplaceWithWorkspaceAsync(msbuildWorkspace, cancellationToken).ConfigureAwait(false);

            List<LoadedProjectInfo> loadedProjects = [];
            foreach (Microsoft.CodeAnalysis.Project project in solution.Projects)
            {
                if (project.Language != LanguageNames.CSharp)
                {
                    continue;
                }

                string targetFramework = "";
                ImmutableArray<string> sourceFiles = project.Documents
                    .Where(d => !string.IsNullOrEmpty(d.FilePath))
                    .Select(d => d.FilePath!)
                    .ToImmutableArray();

                ImmutableArray<string> projectReferencePaths = project.ProjectReferences
                    .Select(pr => solution.GetProject(pr.ProjectId)?.FilePath)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => p!)
                    .ToImmutableArray();

                loadedProjects.Add(new LoadedProjectInfo(
                    project.FilePath ?? project.Name,
                    project.Name,
                    targetFramework,
                    project.Id,
                    sourceFiles,
                    projectReferencePaths));
            }

            return new LoadedSolutionInfo(
                solutionPath,
                Path.GetFileNameWithoutExtension(solutionPath),
                loadedProjects.ToImmutableArray());
        }
        catch
        {
            // On failure, dispose the workspace we just created so the caller can retry
            msbuildWorkspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fallback: loads a .sln file using manual MSBuild evaluation.
    /// </summary>
    private static async Task<LoadedSolutionInfo> LoadSolutionManuallyAsync(string solutionPath, RoslynWorkspaceService workspaceService, CancellationToken cancellationToken)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var projectPaths = ParseSolutionProjectPaths(solutionPath, solutionDir);
        var loadedProjects = new List<LoadedProjectInfo>();

        await workspaceService.ClearWorkspaceAsync(cancellationToken).ConfigureAwait(false);

        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                var projectInfo = await LoadProjectInternalAsync(projectPath, workspaceService, cancellationToken).ConfigureAwait(false);
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

        // Wire up project-to-project references now that all projects are loaded
        await ResolveProjectReferencesAsync(loadedProjects, workspaceService, cancellationToken).ConfigureAwait(false);

        // Clean up the global project collection to avoid stale state
        ProjectCollection.GlobalProjectCollection.UnloadAllProjects();

        return new LoadedSolutionInfo(
            solutionPath,
            Path.GetFileNameWithoutExtension(solutionPath),
            loadedProjects.ToImmutableArray());
    }

    /// <summary>
    /// Loads a single .csproj file into the workspace.
    /// Attempts high-fidelity loading via <c>MSBuildWorkspace</c> first, then falls back
    /// to manual MSBuild evaluation if that fails.
    /// </summary>
    public static async Task<LoadedProjectInfo?> LoadProjectAsync(string projectPath, RoslynWorkspaceService workspaceService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(workspaceService);

        EnsureMSBuildRegistered();

        try
        {
            return await LoadProjectWithMSBuildWorkspaceAsync(projectPath, workspaceService, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"MSBuildWorkspace project load failed, falling back to manual loading: {ex.Message}");
            return await LoadProjectManuallyAsync(projectPath, workspaceService, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads a project using <c>MSBuildWorkspace</c> for full-fidelity project loading.
    /// </summary>
    private static async Task<LoadedProjectInfo?> LoadProjectWithMSBuildWorkspaceAsync(
        string projectPath, RoslynWorkspaceService workspaceService, CancellationToken cancellationToken)
    {
        MSBuildWorkspace msbuildWorkspace = MSBuildWorkspace.Create();
        try
        {
            Microsoft.CodeAnalysis.Project project = await msbuildWorkspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (WorkspaceDiagnostic diagnostic in msbuildWorkspace.Diagnostics)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"MSBuildWorkspace [{diagnostic.Kind}]: {diagnostic.Message}");
            }

            await workspaceService.ReplaceWithWorkspaceAsync(msbuildWorkspace, cancellationToken).ConfigureAwait(false);

            ImmutableArray<string> sourceFiles = project.Documents
                .Where(d => !string.IsNullOrEmpty(d.FilePath))
                .Select(d => d.FilePath!)
                .ToImmutableArray();

            Microsoft.CodeAnalysis.Solution solution = msbuildWorkspace.CurrentSolution;
            ImmutableArray<string> projectReferencePaths = project.ProjectReferences
                .Select(pr => solution.GetProject(pr.ProjectId)?.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToImmutableArray();

            return new LoadedProjectInfo(
                project.FilePath ?? projectPath,
                project.Name,
                "",
                project.Id,
                sourceFiles,
                projectReferencePaths);
        }
        catch
        {
            msbuildWorkspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fallback: loads a single .csproj file using manual MSBuild evaluation.
    /// </summary>
    private static async Task<LoadedProjectInfo?> LoadProjectManuallyAsync(string projectPath, RoslynWorkspaceService workspaceService, CancellationToken cancellationToken = default)
    {
        await workspaceService.ClearWorkspaceAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var info = await LoadProjectInternalAsync(projectPath, workspaceService, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
                return null;
            }

            // Load referenced projects and wire up references
            var allLoaded = new List<LoadedProjectInfo> { info };
            await LoadReferencedProjectsRecursiveAsync(info, allLoaded, workspaceService, cancellationToken).ConfigureAwait(false);
            await ResolveProjectReferencesAsync(allLoaded, workspaceService, cancellationToken).ConfigureAwait(false);

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

    private static async Task<LoadedProjectInfo?> LoadProjectInternalAsync(string projectPath, RoslynWorkspaceService workspaceService, CancellationToken cancellationToken)
    {
        var project = new MSBuildProject(projectPath, null, null, ProjectCollection.GlobalProjectCollection,
            ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);

        var projectDir = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        // Extract compilation settings
        var targetFramework = project.GetPropertyValue("TargetFramework");

        // Support multi-targeting: when TargetFramework is empty, read TargetFrameworks (plural)
        // and pick the first TFM, then re-evaluate the project with that specific framework.
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            string? firstTfm = GetFirstTargetFramework(project.GetPropertyValue("TargetFrameworks"));
            if (firstTfm is not null)
            {
                targetFramework = firstTfm;
                var globalProperties = new Dictionary<string, string> { ["TargetFramework"] = targetFramework };
                project = new MSBuildProject(projectPath, globalProperties, null, ProjectCollection.GlobalProjectCollection,
                    ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);
            }
        }
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

        // Parse language version — fall back to the TFM default rather than LanguageVersion.Latest
        if (!LanguageVersionFacts.TryParse(langVersion, out var languageVersion))
        {
            languageVersion = GetDefaultLanguageVersionForTfm(targetFramework);
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
        var addedSourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSourceFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (addedSourceFiles.Add(path))
            {
                sourceFiles.Add(path);
            }
        }

        foreach (var item in project.GetItems("Compile"))
        {
            var filePath = Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude));
            AddSourceFile(filePath);
        }

        var configuration = project.GetPropertyValue("Configuration");
        foreach (var generatedSourceFile in GetGeneratedSourceFiles(projectDir, targetFramework, configuration))
        {
            AddSourceFile(generatedSourceFile);
        }

        // Collect ProjectReference paths for cross-project resolution
        var projectReferencePaths = new List<string>();
        foreach (var item in project.GetItems("ProjectReference"))
        {
            var refPath = Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude));
            projectReferencePaths.Add(refPath);
        }

        // Collect metadata references — prefer design-time build resolution, fall back to manual scanning
        var metadataReferences = ResolveMetadataReferencesViaBuild(project, targetFramework)
            ?? ResolveMetadataReferences(project, projectDir, targetFramework);

        // Collect analyzer references — prefer design-time build resolution, fall back to manual scanning
        var analyzerReferences = ResolveAnalyzerReferencesViaBuild(project)
            ?? ResolveAnalyzerReferences(project, targetFramework);

        // Collect preprocessor symbols
        var defineConstants = project.GetPropertyValue("DefineConstants");
        if (!string.IsNullOrEmpty(defineConstants))
        {
            var symbols = defineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            parseOptions = parseOptions.WithPreprocessorSymbols((IEnumerable<string>)symbols);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Register the project in the workspace
        var projectId = await workspaceService.AddProjectAsync(
            assemblyName,
            compilationOptions,
            parseOptions,
            metadataReferences,
            analyzerReferences,
            cancellationToken).ConfigureAwait(false);

        // Add source files
        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = File.ReadAllText(sourceFile);
            await workspaceService.AddDocumentToProjectAsync(projectId, sourceFile, text, cancellationToken).ConfigureAwait(false);
        }

        // Add additional (non-compiled) files so analyzers can access them
        var additionalFiles = CollectAdditionalFiles(project, projectDir);
        if (additionalFiles.Count > 0)
        {
            await workspaceService.AddAdditionalDocumentsToProjectAsync(projectId, additionalFiles, cancellationToken).ConfigureAwait(false);
        }

        // Add .editorconfig files so style/analyzer severity settings are respected
        var editorConfigFiles = CollectEditorConfigFiles(projectDir);
        if (editorConfigFiles.Count > 0)
        {
            await workspaceService.AddAnalyzerConfigDocumentsToProjectAsync(projectId, editorConfigFiles, cancellationToken).ConfigureAwait(false);
        }

        return new LoadedProjectInfo(
            projectPath,
            projectName,
            targetFramework,
            projectId,
            sourceFiles.ToImmutableArray(),
            projectReferencePaths.ToImmutableArray());
    }

    /// <summary>
    /// Attempts to resolve metadata references by running the MSBuild <c>ResolveAssemblyReferences</c>
    /// design-time target. Returns null if the build fails, allowing the caller to fall back.
    /// </summary>
    private static List<MetadataReference>? ResolveMetadataReferencesViaBuild(MSBuildProject msbuildProject, string targetFramework)
    {
        try
        {
            var instance = msbuildProject.CreateProjectInstance();
            instance.SetProperty("DesignTimeBuild", "true");
            instance.SetProperty("SkipCompilerExecution", "true");

            bool success = instance.Build(["ResolveAssemblyReferences"], null);
            if (!success)
            {
                return null;
            }

            var references = new List<MetadataReference>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in instance.GetItems("ReferencePath"))
            {
                string assemblyPath = item.EvaluatedInclude;
                if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
                {
                    continue;
                }

                if (addedPaths.Add(assemblyPath))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(assemblyPath));
                    }
                    catch
                    {
                        // Skip assemblies that cannot be loaded (e.g., native shims)
                    }
                }
            }

            if (references.Count == 0)
            {
                return null;
            }

            // The design-time build may not include all framework assemblies; ensure they are present.
            AddFrameworkReferences(references, addedPaths, targetFramework);

            return references;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Design-time build failed for ResolveAssemblyReferences: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the first target framework from a semicolon-separated <c>TargetFrameworks</c> value,
    /// or null if the value is empty.
    /// </summary>
    internal static string? GetFirstTargetFramework(string? targetFrameworks)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return null;
        }

        string[] tfms = targetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tfms.Length > 0 ? tfms[0] : null;
    }

    /// <summary>
    /// Maps a target framework moniker to the default C# language version defined by the SDK,
    /// instead of unconditionally using <see cref="LanguageVersion.Latest"/>.
    /// </summary>
    internal static LanguageVersion GetDefaultLanguageVersionForTfm(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return LanguageVersion.Latest;
        }

        int? major = ParseTfmMajorVersion(targetFramework);
        return major switch
        {
            5 => LanguageVersion.CSharp9,
            6 => LanguageVersion.CSharp10,
            7 => LanguageVersion.CSharp11,
            8 => LanguageVersion.CSharp12,
            9 => LanguageVersion.CSharp13,
            >= 10 => LanguageVersion.Preview,
            _ => LanguageVersion.Latest
        };
    }

    /// <summary>
    /// Extracts the major version number from a TFM such as "net8.0" or "net8.0-windows7.0".
    /// Returns null for non-numeric or legacy TFMs (e.g. "netstandard2.0", "net48").
    /// </summary>
    private static int? ParseTfmMajorVersion(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return null;
        }

        // Only modern .NET TFMs (net5.0+) use the netX.Y format
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ReadOnlySpan<char> tfm = targetFramework.AsSpan()[3..];

        // Strip platform suffix (e.g., "8.0-windows7.0" ? "8.0")
        int dashIndex = tfm.IndexOf('-');
        if (dashIndex >= 0)
        {
            tfm = tfm[..dashIndex];
        }

        // Modern .NET TFMs always contain a dot (e.g., "net8.0").
        // .NET Framework TFMs do not (e.g., "net48", "net472").
        int dotIndex = tfm.IndexOf('.');
        if (dotIndex < 0)
        {
            return null;
        }

        ReadOnlySpan<char> majorSpan = tfm[..dotIndex];

        return int.TryParse(majorSpan, out int major) && major >= 5 ? major : null;
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
        var frameworkRefs = SharedFrameworkResolver.GetFrameworkReferences(targetFramework);
        foreach (var reference in frameworkRefs)
        {
            var path = reference.Display ?? string.Empty;
            if (!string.IsNullOrEmpty(path) && addedPaths.Add(path))
            {
                references.Add(reference);
            }
        }
    }

    /// <summary>
    /// Attempts to discover analyzer assemblies by running the MSBuild
    /// <c>ResolvePackageAssets</c> target and collecting <c>Analyzer</c> items.
    /// Returns null if the build fails, allowing the caller to fall back.
    /// </summary>
    private static List<AnalyzerReference>? ResolveAnalyzerReferencesViaBuild(MSBuildProject msbuildProject)
    {
        try
        {
            var instance = msbuildProject.CreateProjectInstance();
            instance.SetProperty("DesignTimeBuild", "true");
            instance.SetProperty("SkipCompilerExecution", "true");

            // ResolvePackageAssets populates the Analyzer item group from restored packages
            bool success = instance.Build(["ResolvePackageAssets"], null);
            if (!success)
            {
                return null;
            }

            var references = new List<AnalyzerReference>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in instance.GetItems("Analyzer"))
            {
                string analyzerPath = item.EvaluatedInclude;
                if (string.IsNullOrEmpty(analyzerPath) || !File.Exists(analyzerPath))
                {
                    continue;
                }

                if (!analyzerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (addedPaths.Add(analyzerPath))
                {
                    references.Add(new AnalyzerFileReference(analyzerPath, s_analyzerLoader));
                }
            }

            return references.Count > 0 ? references : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Design-time build failed for ResolvePackageAssets (analyzers): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Discovers analyzer assemblies by scanning the <c>analyzers</c> folder of each
    /// <c>PackageReference</c> in the NuGet global packages cache.
    /// </summary>
    internal static List<AnalyzerReference> ResolveAnalyzerReferences(
        MSBuildProject msbuildProject, string targetFramework)
    {
        var references = new List<AnalyzerReference>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in msbuildProject.GetItems("PackageReference"))
        {
            var packageName = item.EvaluatedInclude;
            var version = item.GetMetadataValue("Version");
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(version))
            {
                continue;
            }

            ResolvePackageAnalyzers(references, addedPaths, packageName, version, targetFramework);
        }

        return references;
    }

    /// <summary>
    /// Scans a single NuGet package for analyzer DLLs under its <c>analyzers</c> directory.
    /// Prefers the <c>analyzers/dotnet/cs</c> sub-path, then <c>analyzers/dotnet</c>,
    /// then the root <c>analyzers</c> folder.
    /// </summary>
    internal static void ResolvePackageAnalyzers(
        List<AnalyzerReference> references,
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

        var analyzersDir = Path.Combine(packageDir, "analyzers");
        if (!Directory.Exists(analyzersDir))
        {
            return;
        }

        // Prefer the most specific path: analyzers/dotnet/cs > analyzers/dotnet > analyzers
        string? bestDir = FindBestAnalyzerDirectory(analyzersDir);
        if (bestDir is null)
        {
            return;
        }

        foreach (var dll in Directory.EnumerateFiles(bestDir, "*.dll"))
        {
            if (addedPaths.Add(dll))
            {
                references.Add(new AnalyzerFileReference(dll, s_analyzerLoader));
            }
        }
    }

    /// <summary>
    /// Finds the best analyzer subdirectory for C#. Prefers <c>dotnet/cs</c>, then <c>dotnet</c>,
    /// then the root analyzers folder.
    /// </summary>
    internal static string? FindBestAnalyzerDirectory(string analyzersDir)
    {
        // Most NuGet analyzer packages use: analyzers/dotnet/cs
        var csDir = Path.Combine(analyzersDir, "dotnet", "cs");
        if (Directory.Exists(csDir) && Directory.EnumerateFiles(csDir, "*.dll").Any())
        {
            return csDir;
        }

        // Some packages use: analyzers/dotnet
        var dotnetDir = Path.Combine(analyzersDir, "dotnet");
        if (Directory.Exists(dotnetDir) && Directory.EnumerateFiles(dotnetDir, "*.dll").Any())
        {
            return dotnetDir;
        }

        // Fall back to the root analyzers folder
        if (Directory.EnumerateFiles(analyzersDir, "*.dll").Any())
        {
            return analyzersDir;
        }

        return null;
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

    private static IReadOnlyList<string> GetGeneratedSourceFiles(
        string projectDir,
        string targetFramework,
        string configuration)
    {
        var objDir = Path.Combine(projectDir, "obj");
        if (!Directory.Exists(objDir))
        {
            return [];
        }

        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configuration) && !string.IsNullOrWhiteSpace(targetFramework))
        {
            var configuredTargetDir = Path.Combine(objDir, configuration, targetFramework);
            if (Directory.Exists(configuredTargetDir))
            {
                candidateDirs.Add(configuredTargetDir);
            }

            var configurationDir = Path.Combine(objDir, configuration);
            var bestConfiguredTfmDir = FindBestTfmDirectory(configurationDir, targetFramework);
            if (!string.IsNullOrWhiteSpace(bestConfiguredTfmDir))
            {
                candidateDirs.Add(bestConfiguredTfmDir);
            }
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            foreach (var dir in Directory.EnumerateDirectories(objDir, targetFramework, SearchOption.AllDirectories))
            {
                candidateDirs.Add(dir);
            }
        }

        var generatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in candidateDirs)
        {
            // Search all subdirectories for *.g.cs and *.g.i.cs patterns
            foreach (var pattern in s_generatedSourcePatterns)
            {
                foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                {
                    generatedFiles.Add(file);
                }
            }

            // Include all .cs files under "generated" subdirectories (source generator output)
            string generatedDir = Path.Combine(dir, "generated");
            if (Directory.Exists(generatedDir))
            {
                foreach (var file in Directory.EnumerateFiles(generatedDir, "*.cs", SearchOption.AllDirectories))
                {
                    generatedFiles.Add(file);
                }
            }
        }

        // Implicit usings are generated into *.GlobalUsings.g.cs; include them broadly
        // so analysis stays accurate even when TFM directory naming differs.
        foreach (var globalUsingsFile in Directory.EnumerateFiles(objDir, "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
        {
            generatedFiles.Add(globalUsingsFile);
        }

        return generatedFiles
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
    /// Wires up Roslyn <see cref="ProjectReference"/>s between loaded projects
    /// by matching each project's <c>ProjectReference</c> paths to already-loaded project paths.
    /// </summary>
    private static async Task ResolveProjectReferencesAsync(
        List<LoadedProjectInfo> loadedProjects,
        RoslynWorkspaceService workspaceService,
        CancellationToken cancellationToken)
    {
        // Build a lookup from absolute project path ? loaded info
        var pathToProject = new Dictionary<string, LoadedProjectInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in loadedProjects)
        {
            pathToProject[project.ProjectPath] = project;
        }

        foreach (var project in loadedProjects)
        {
            if (project.ProjectReferencePaths.IsEmpty)
            {
                continue;
            }

            var referencedIds = new List<ProjectId>();
            foreach (var refPath in project.ProjectReferencePaths)
            {
                if (pathToProject.TryGetValue(refPath, out var referencedProject))
                {
                    referencedIds.Add(referencedProject.RoslynProjectId);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"ProjectReference '{refPath}' from '{project.Name}' was not loaded — skipping.");
                }
            }

            if (referencedIds.Count > 0)
            {
                await workspaceService.AddProjectReferencesAsync(
                    project.RoslynProjectId, referencedIds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Recursively loads projects referenced by <paramref name="project"/> that have not yet been loaded.
    /// Used when opening a single .csproj that references other projects.
    /// </summary>
    private static async Task LoadReferencedProjectsRecursiveAsync(
        LoadedProjectInfo project,
        List<LoadedProjectInfo> allLoaded,
        RoslynWorkspaceService workspaceService,
        CancellationToken cancellationToken)
    {
        var loadedPaths = new HashSet<string>(
            allLoaded.Select(p => p.ProjectPath), StringComparer.OrdinalIgnoreCase);

        foreach (var refPath in project.ProjectReferencePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (loadedPaths.Contains(refPath))
            {
                continue;
            }

            if (!File.Exists(refPath))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Referenced project not found: '{refPath}' — skipping.");
                continue;
            }

            var ext = Path.GetExtension(refPath);
            if (!ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var refInfo = await LoadProjectInternalAsync(refPath, workspaceService, cancellationToken).ConfigureAwait(false);
                if (refInfo is not null)
                {
                    allLoaded.Add(refInfo);
                    loadedPaths.Add(refPath);

                    // Recurse into this project's references
                    await LoadReferencedProjectsRecursiveAsync(
                        refInfo, allLoaded, workspaceService, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load referenced project '{refPath}': {ex.Message}");
            }
        }
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

    /// <summary>
    /// Collects <c>AdditionalFiles</c> items from the MSBuild project evaluation.
    /// These are non-compiled files passed to analyzers (e.g. <c>PublicAPI.Shipped.txt</c>).
    /// </summary>
    internal static IReadOnlyList<string> CollectAdditionalFiles(MSBuildProject msbuildProject, string projectDir)
    {
        ArgumentNullException.ThrowIfNull(msbuildProject);

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in msbuildProject.GetItems("AdditionalFiles"))
        {
            var filePath = Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude));
            if (File.Exists(filePath) && seen.Add(filePath))
            {
                files.Add(filePath);
            }
        }

        return files;
    }

    /// <summary>
    /// Discovers <c>.editorconfig</c> files by walking from <paramref name="projectDir"/>
    /// up to the filesystem root. Stops early if a file contains <c>root = true</c>.
    /// </summary>
    internal static IReadOnlyList<string> CollectEditorConfigFiles(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return [];
        }

        var files = new List<string>();
        DirectoryInfo? dir = new DirectoryInfo(projectDir);

        while (dir is not null)
        {
            string editorConfigPath = Path.Combine(dir.FullName, ".editorconfig");
            if (File.Exists(editorConfigPath))
            {
                files.Add(editorConfigPath);

                if (IsRootEditorConfig(editorConfigPath))
                {
                    break;
                }
            }

            dir = dir.Parent;
        }

        return files;
    }

    /// <summary>
    /// Returns true if the <c>.editorconfig</c> file declares <c>root = true</c>.
    /// </summary>
    private static bool IsRootEditorConfig(string editorConfigPath)
    {
        try
        {
            foreach (string line in File.ReadLines(editorConfigPath))
            {
                ReadOnlySpan<char> trimmed = line.AsSpan().Trim();

                // Skip comments and empty lines
                if (trimmed.IsEmpty || trimmed[0] == '#' || trimmed[0] == ';')
                {
                    continue;
                }

                // Section headers end the preamble where root = true is valid
                if (trimmed[0] == '[')
                {
                    break;
                }

                // Check for "root = true" (case-insensitive, whitespace around '=' allowed)
                int equalsIndex = trimmed.IndexOf('=');
                if (equalsIndex < 0)
                {
                    continue;
                }

                ReadOnlySpan<char> key = trimmed[..equalsIndex].Trim();
                ReadOnlySpan<char> value = trimmed[(equalsIndex + 1)..].Trim();

                if (key.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                    value.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to read .editorconfig '{editorConfigPath}': {ex.Message}");
        }

        return false;
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
/// Information about a loaded project, including paths to referenced projects.
/// </summary>
internal sealed record LoadedProjectInfo(
    string ProjectPath,
    string Name,
    string TargetFramework,
    ProjectId RoslynProjectId,
    ImmutableArray<string> SourceFiles,
    ImmutableArray<string> ProjectReferencePaths);
