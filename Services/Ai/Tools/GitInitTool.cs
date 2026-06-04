using System.IO;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that initializes a new Git repository at the project root.
/// Creates a .gitignore file with standard Visual Studio/C# entries.
/// </summary>
internal sealed class GitInitTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {},
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;
    private readonly Func<GitService?> _gitServiceProvider;
    private readonly Action _onRepositoryChanged;

    public GitInitTool(
        Func<string?> projectRootProvider,
        Func<GitService?> gitServiceProvider,
        Action onRepositoryChanged)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        ArgumentNullException.ThrowIfNull(onRepositoryChanged);
        _projectRootProvider = projectRootProvider;
        _gitServiceProvider = gitServiceProvider;
        _onRepositoryChanged = onRepositoryChanged;
    }

    public string Name => "git_init";

    public string Description => "Initializes a new Git repository in the project root directory. Creates a standard .gitignore for C# / Visual Studio projects. Only available when no Git repository is currently open.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var git = _gitServiceProvider();
        if (git is not null && git.IsRepositoryOpen)
        {
            return Task.FromResult(ToolCallResult.Fail("A Git repository is already open."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string projectRoot;
        try
        {
            projectRoot = AgentToolPathResolver.GetProjectRootDirectory(_projectRootProvider);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (!Directory.Exists(projectRoot))
        {
            return Task.FromResult(ToolCallResult.Fail("Project root directory does not exist."));
        }

        try
        {
            bool initialized = git is not null && git.TryInitializeRepository(projectRoot);
            if (!initialized)
            {
                return Task.FromResult(ToolCallResult.Fail("Failed to initialize Git repository. The directory may already be part of a repository."));
            }

            _onRepositoryChanged();
            return Task.FromResult(ToolCallResult.Ok($"Initialized empty Git repository in '{projectRoot}'. A .gitignore file was created."));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
    }
}