using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that discards working-directory changes to a file in Git.
/// Untracked new files are deleted; tracked modified/deleted files are restored from HEAD.
/// </summary>
internal sealed class GitDiscardTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "Repository-relative path of the file whose changes should be discarded."
                }
            },
            "required": ["filePath"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitDiscardTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_discard";

    public string Description => "Discards working-directory changes to a specific file. Untracked new files are deleted from disk. Tracked modified or deleted files are restored from the HEAD commit. Staged changes are preserved.";

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

        if (!arguments.TryGetProperty("filePath", out var fpElement) ||
            string.IsNullOrWhiteSpace(fpElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string filePath = fpElement.GetString()!.Trim();
            git.DiscardFile(filePath);
            return Task.FromResult(ToolCallResult.Ok($"Discarded changes to: {filePath}"));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
    }
}