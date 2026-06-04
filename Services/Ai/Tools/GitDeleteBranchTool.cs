using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that deletes an existing local Git branch.
/// Cannot delete the currently checked-out branch.
/// </summary>
internal sealed class GitDeleteBranchTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "branchName": {
                    "type": "string",
                    "description": "The name of the branch to delete (cannot be the currently checked-out branch)."
                }
            },
            "required": ["branchName"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitDeleteBranchTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_delete_branch";

    public string Description => "Deletes an existing local Git branch. The currently checked-out branch cannot be deleted.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var git = _gitServiceProvider();
        if (git is null || !git.IsRepositoryOpen)
        {
            return ToolCallResult.Fail("No Git repository is currently open.");
        }

        if (!arguments.TryGetProperty("branchName", out var branchElement) ||
            string.IsNullOrWhiteSpace(branchElement.GetString()))
        {
            return ToolCallResult.Fail("Missing required parameter: branchName");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string branchName = branchElement.GetString()!.Trim();
            await git.DeleteBranchAsync(branchName, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Ok($"Deleted branch: {branchName}");
        }
        catch (InvalidOperationException ex)
        {
            return ToolCallResult.Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ToolCallResult.Fail(ex.Message);
        }
    }
}