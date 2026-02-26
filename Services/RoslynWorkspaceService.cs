using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace KaneCode.Services;

/// <summary>
/// Manages an in-memory Roslyn workspace for C# files to power
/// semantic highlighting, completion, and diagnostics.
/// </summary>
internal sealed class RoslynWorkspaceService : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private ProjectId _projectId;
    private readonly ConcurrentDictionary<string, DocumentId> _documentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProjectId> _projectIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public RoslynWorkspaceService()
    {
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        _workspace = new AdhocWorkspace(host);

        InitializeDefaultProject();
    }

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

        _workspace.AddProject(projectInfo);
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
    public DocumentId OpenOrUpdateDocument(string filePath, string text)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(text);

        var sourceText = SourceText.From(text);

        if (_documentIds.TryGetValue(filePath, out var existingId))
        {
            var currentSolution = _workspace.CurrentSolution;
            var updatedSolution = currentSolution.WithDocumentText(existingId, sourceText);
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
    public void UpdateDocumentText(string filePath, string text)
    {
        if (!_documentIds.TryGetValue(filePath, out var documentId))
        {
            return;
        }

        var sourceText = SourceText.From(text);
        var updatedSolution = _workspace.CurrentSolution.WithDocumentText(documentId, sourceText);
        _workspace.TryApplyChanges(updatedSolution);
    }

    /// <summary>
    /// Removes a document from the workspace.
    /// </summary>
    public void CloseDocument(string filePath)
    {
        if (!_documentIds.TryRemove(filePath, out var documentId))
        {
            return;
        }

        _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveDocument(documentId));
    }

    /// <summary>
    /// Gets the Roslyn <see cref="Document"/> for the given file path.
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
    /// Gets diagnostics for the given file.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var document = GetDocument(filePath);
        if (document is null)
        {
            return [];
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return [];
        }

        return semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Returns true if the workspace has any loaded MSBuild projects (beyond the default adhoc project).
    /// </summary>
    public bool HasLoadedProjects => !_projectIds.IsEmpty;

    /// <summary>
    /// Removes all projects and documents from the workspace and re-creates the default project.
    /// </summary>
    public void ClearWorkspace()
    {
        var solution = _workspace.CurrentSolution;
        foreach (var projectId in solution.ProjectIds)
        {
            solution = solution.RemoveProject(projectId);
        }

        _workspace.TryApplyChanges(solution);
        _documentIds.Clear();
        _projectIds.Clear();

        // Re-create the default fallback project for loose files
        InitializeDefaultProject();
    }

    /// <summary>
    /// Adds a new project to the workspace with the given compilation settings.
    /// </summary>
    public ProjectId AddProject(
        string projectName,
        CSharpCompilationOptions compilationOptions,
        CSharpParseOptions parseOptions,
        IEnumerable<MetadataReference> metadataReferences)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        ArgumentNullException.ThrowIfNull(compilationOptions);
        ArgumentNullException.ThrowIfNull(parseOptions);

        var projectId = ProjectId.CreateNewId(projectName);
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            projectName,
            projectName,
            LanguageNames.CSharp,
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            metadataReferences: metadataReferences.ToList());

        _workspace.TryApplyChanges(_workspace.CurrentSolution.AddProject(projectInfo));
        _projectIds[projectName] = projectId;
        return projectId;
    }

    /// <summary>
    /// Adds a source document to a specific project.
    /// </summary>
    public DocumentId AddDocumentToProject(ProjectId projectId, string filePath, string text)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(text);

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

    public void Dispose()
    {
        if (!_disposed)
        {
            _workspace.Dispose();
            _disposed = true;
        }
    }

    private static List<MetadataReference> GetDefaultReferences()
    {
        var references = new List<MetadataReference>();
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;

        if (string.IsNullOrEmpty(trustedAssemblies))
        {
            // Fallback: add at least the core runtime reference
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            AddReferenceIfExists(references, Path.Combine(runtimeDir, "System.Runtime.dll"));
            AddReferenceIfExists(references, Path.Combine(runtimeDir, "System.Console.dll"));
            AddReferenceIfExists(references, Path.Combine(runtimeDir, "System.Collections.dll"));
            AddReferenceIfExists(references, Path.Combine(runtimeDir, "System.Linq.dll"));
            AddReferenceIfExists(references, Path.Combine(runtimeDir, "mscorlib.dll"));
            AddReferenceIfExists(references, Path.Combine(runtimeDir, "netstandard.dll"));
            return references;
        }

        foreach (var assemblyPath in trustedAssemblies.Split(Path.PathSeparator))
        {
            AddReferenceIfExists(references, assemblyPath);
        }

        return references;
    }

    private static void AddReferenceIfExists(List<MetadataReference> references, string path)
    {
        if (File.Exists(path))
        {
            references.Add(MetadataReference.CreateFromFile(path));
        }
    }
}
