using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;

namespace KaneCode.Services;

/// <summary>
/// Manages an in-memory Roslyn workspace for C# files to power
/// semantic highlighting, completion, and diagnostics.
/// </summary>
internal sealed class RoslynWorkspaceService : IDisposable
{
    private readonly MefHostServices _hostServices;
    private Workspace _workspace;
    private readonly SemaphoreSlim _workspaceLock = new(1, 1);
    private ProjectId _projectId;
    private readonly ConcurrentDictionary<string, DocumentId> _documentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProjectId> _projectIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public RoslynWorkspaceService()
    {
        _hostServices = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        _workspace = new AdhocWorkspace(_hostServices);

        InitializeDefaultProject();
    }

    /// <remarks>Must only be called while <see cref="_workspaceLock"/> is held, or during construction.</remarks>
    private void InitializeDefaultProject()
    {
        _projectId = ProjectId.CreateNewId("KaneCodeProject");
        var projectInfo = ProjectInfo.Create(
            _projectId,
            VersionStamp.Default,
            "KaneCodeProject",
            "KaneCodeProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: GetDefaultReferences());

        _workspace.TryApplyChanges(_workspace.CurrentSolution.AddProject(projectInfo));
    }

    /// <summary>
    /// The underlying Roslyn workspace.
    /// </summary>
    public Workspace Workspace => _workspace;

    /// <summary>
    /// Returns true if the given file path is a C# source file.
    /// </summary>
    public static bool IsCSharpFile(string filePath)
    {
        return Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens or updates a document in the workspace.
    /// </summary>
    public async Task<DocumentId> OpenOrUpdateDocumentAsync(string filePath, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(text);

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return OpenOrUpdateDocumentUnsafe(filePath, text);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Opens or updates a document without acquiring the lock.
    /// The caller must already hold <see cref="_workspaceLock"/>.
    /// </summary>
    private DocumentId OpenOrUpdateDocumentUnsafe(string filePath, string text)
    {
        var sourceText = SourceText.From(text);

        if (_documentIds.TryGetValue(filePath, out var existingId))
        {
            var updatedSolution = _workspace.CurrentSolution.WithDocumentText(existingId, sourceText);
            _workspace.TryApplyChanges(updatedSolution);
            return existingId;
        }

        var documentId = DocumentId.CreateNewId(_projectId, filePath);
        var fileName = Path.GetFileName(filePath);

        var documentInfo = DocumentInfo.Create(
            documentId,
            fileName,
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())),
            filePath: filePath);

        _workspace.TryApplyChanges(_workspace.CurrentSolution.AddDocument(documentInfo));
        _documentIds[filePath] = documentId;
        return documentId;
    }

    /// <summary>
    /// Updates the text of a document already tracked in the workspace.
    /// </summary>
    public async Task UpdateDocumentTextAsync(string filePath, string text, CancellationToken cancellationToken = default)
    {
        if (!_documentIds.TryGetValue(filePath, out var documentId))
        {
            return;
        }

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sourceText = SourceText.From(text);
            var updatedSolution = _workspace.CurrentSolution.WithDocumentText(documentId, sourceText);
            _workspace.TryApplyChanges(updatedSolution);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Removes a document from the workspace.
    /// </summary>
    public async Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_documentIds.TryRemove(filePath, out var documentId))
        {
            return;
        }

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveDocument(documentId));
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Gets the Roslyn <see cref="Document"/> for the given file path.
    /// Thread-safe: reads an immutable solution snapshot.
    /// </summary>
    public Document? GetDocument(string filePath)
    {
        if (!_documentIds.TryGetValue(filePath, out var documentId))
        {
            return null;
        }

        return _workspace.CurrentSolution.GetDocument(documentId);
    }

    /// <summary>
    /// Gets diagnostics for the given file, including both compiler diagnostics
    /// and analyzer diagnostics from any registered <see cref="AnalyzerReference"/> entries.
    /// Thread-safe: operates on an immutable solution snapshot.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Document? document = GetDocument(filePath);
        if (document is null)
        {
            return [];
        }

        SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return [];
        }

        ImmutableArray<Diagnostic> compilerDiagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);

        ImmutableArray<Diagnostic> analyzerDiagnostics = await GetAnalyzerDiagnosticsForDocumentAsync(
            document, cancellationToken).ConfigureAwait(false);

        if (analyzerDiagnostics.IsEmpty)
        {
            return compilerDiagnostics;
        }

        var combined = new List<Diagnostic>(compilerDiagnostics.Length + analyzerDiagnostics.Length);
        combined.AddRange(compilerDiagnostics);
        combined.AddRange(analyzerDiagnostics);
        return combined;
    }

    /// <summary>
    /// Runs all analyzers registered on the document's project and returns diagnostics
    /// scoped to the given document's syntax tree.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsForDocumentAsync(
        Document document, CancellationToken cancellationToken)
    {
        Project project = document.Project;
        if (project.AnalyzerReferences.Count == 0)
        {
            return [];
        }

        Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return [];
        }

        ImmutableArray<DiagnosticAnalyzer> analyzers = project.AnalyzerReferences
            .SelectMany(r => r.GetAnalyzersForAllLanguages())
            .ToImmutableArray();

        if (analyzers.IsEmpty)
        {
            return [];
        }

        SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null)
        {
            return [];
        }

        try
        {
            CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, options: null, cancellationToken: cancellationToken);
            ImmutableArray<Diagnostic> allDiagnostics = await compilationWithAnalyzers
                .GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

            // Filter to diagnostics located in the current document's syntax tree
            return allDiagnostics
                .Where(d => d.Location.SourceTree == tree)
                .ToImmutableArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Analyzer execution failed for '{document.FilePath}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Gets tracked open documents that should be re-analyzed when the specified file changes.
    /// Includes the edited file and any open files in projects that transitively depend on its project.
    /// </summary>
    public IReadOnlyList<string> GetDependentOpenDocumentFilePaths(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!_documentIds.TryGetValue(filePath, out var sourceDocumentId))
        {
            return [filePath];
        }

        var solution = _workspace.CurrentSolution;
        var sourceDocument = solution.GetDocument(sourceDocumentId);
        if (sourceDocument is null)
        {
            return [filePath];
        }

        var dependencyGraph = solution.GetProjectDependencyGraph();
        var projectIdsToReanalyze = dependencyGraph
            .GetProjectsThatTransitivelyDependOnThisProject(sourceDocument.Project.Id)
            .Append(sourceDocument.Project.Id)
            .ToHashSet();

        var dependentPaths = new List<string>();
        foreach (var path in _documentIds.Keys)
        {
            if (!_documentIds.TryGetValue(path, out var documentId))
            {
                continue;
            }

            var document = solution.GetDocument(documentId);
            if (document is null)
            {
                continue;
            }

            if (projectIdsToReanalyze.Contains(document.Project.Id))
            {
                dependentPaths.Add(path);
            }
        }

        if (dependentPaths.Count == 0)
        {
            dependentPaths.Add(filePath);
        }

        return dependentPaths;
    }

    /// <summary>
    /// Returns true if the workspace has any loaded MSBuild projects (beyond the default adhoc project).
    /// </summary>
    public bool HasLoadedProjects => !_projectIds.IsEmpty;

    /// <summary>
    /// Removes all projects and documents from the workspace and re-creates the default project.
    /// </summary>
    public async Task ClearWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _workspace.Dispose();
            _workspace = new AdhocWorkspace(_hostServices);
            _documentIds.Clear();
            _projectIds.Clear();

            InitializeDefaultProject();
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Replaces the backing workspace with an <c>MSBuildWorkspace</c> (or any pre-populated workspace)
    /// and rebuilds the internal document/project tracking dictionaries from its solution.
    /// The caller is responsible for having already loaded projects into <paramref name="newWorkspace"/>.
    /// </summary>
    internal async Task ReplaceWithWorkspaceAsync(Workspace newWorkspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newWorkspace);

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _workspace.Dispose();
            _workspace = newWorkspace;
            _documentIds.Clear();
            _projectIds.Clear();

            // Rebuild tracking dictionaries from the loaded solution
            Solution solution = newWorkspace.CurrentSolution;
            bool firstProject = true;
            foreach (Project project in solution.Projects)
            {
                if (!string.IsNullOrEmpty(project.Name))
                {
                    _projectIds[project.Name] = project.Id;
                }

                // Use the first project as the default target for loose file opens
                if (firstProject)
                {
                    _projectId = project.Id;
                    firstProject = false;
                }

                foreach (Document document in project.Documents)
                {
                    if (!string.IsNullOrEmpty(document.FilePath))
                    {
                        _documentIds[document.FilePath] = document.Id;
                    }
                }
            }
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Adds a new project to the workspace with the given compilation settings.
    /// </summary>
    public async Task<ProjectId> AddProjectAsync(
        string projectName,
        CSharpCompilationOptions compilationOptions,
        CSharpParseOptions parseOptions,
        IEnumerable<MetadataReference> metadataReferences,
        IEnumerable<AnalyzerReference>? analyzerReferences = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        ArgumentNullException.ThrowIfNull(compilationOptions);
        ArgumentNullException.ThrowIfNull(parseOptions);

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projectId = ProjectId.CreateNewId(projectName);
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                projectName,
                projectName,
                LanguageNames.CSharp,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                metadataReferences: metadataReferences.ToList(),
                analyzerReferences: analyzerReferences?.ToList());

            _workspace.TryApplyChanges(_workspace.CurrentSolution.AddProject(projectInfo));
            _projectIds[projectName] = projectId;
            return projectId;
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Adds a source document to a specific project.
    /// </summary>
    public async Task<DocumentId> AddDocumentToProjectAsync(ProjectId projectId, string filePath, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(text);

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sourceText = SourceText.From(text);
            var documentId = DocumentId.CreateNewId(projectId, filePath);
            var fileName = Path.GetFileName(filePath);

            var documentInfo = DocumentInfo.Create(
                documentId,
                fileName,
                loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())),
                filePath: filePath);

            _workspace.TryApplyChanges(_workspace.CurrentSolution.AddDocument(documentInfo));
            _documentIds[filePath] = documentId;
            return documentId;
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Adds project-to-project references so that cross-project types resolve correctly.
    /// </summary>
    public async Task AddProjectReferencesAsync(
        ProjectId fromProjectId,
        IEnumerable<ProjectId> referencedProjectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fromProjectId);
        ArgumentNullException.ThrowIfNull(referencedProjectIds);

        var refs = referencedProjectIds.ToList();
        if (refs.Count == 0)
        {
            return;
        }

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var solution = _workspace.CurrentSolution;
            foreach (var referencedId in refs)
            {
                solution = solution.AddProjectReference(fromProjectId, new ProjectReference(referencedId));
            }

            _workspace.TryApplyChanges(solution);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Looks up the <see cref="ProjectId"/> for a project that was previously added via <see cref="AddProjectAsync"/>.
    /// </summary>
    public ProjectId? GetProjectId(string projectName)
    {
        return _projectIds.TryGetValue(projectName, out var id) ? id : null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _workspaceLock.Dispose();
            _workspace.Dispose();
            _disposed = true;
        }
    }

    private static List<MetadataReference> GetDefaultReferences()
    {
        return SharedFrameworkResolver.GetFrameworkReferences(targetFramework: null);
    }
}
