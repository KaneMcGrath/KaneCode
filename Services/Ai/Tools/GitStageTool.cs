using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that stages files in Git (adds them to the index).
/// Can stage a single file or all unstaged/untracked files.
/// </summary>
internal sealed class GitStageTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "Repository-relative path of the file to stage. If omitted, all unstaged and untracked files are staged."
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitStageTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_stage";

    public string Description => "Stages a file (or all files) in Git, adding them to the staging area/index so they can be committed. If no filePath is provided, all unstaged and untracked files are staged.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var git = _gitServiceProvider();
        if (git is null || !git.IsRepositoryOpen)
        {
            return Task.FromResult(ToolCallResult.Fail("No Git repository is currently open."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (arguments.TryGetProperty("filePath", out var fpElement) &&
                fpElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(fpElement.GetString()))
            {
                string filePath = fpElement.GetString()!.Trim();
                git.StageFile(filePath);
                return Task.FromResult(ToolCallResult.Ok($"Staged: {filePath}"));
            }
            else
            {
                git.StageAll();
                return Task.FromResult(ToolCallResult.Ok("Staged all unstaged and untracked files."));
            }
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
    }
}