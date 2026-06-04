using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that creates a new local Git branch from the current HEAD.
/// </summary>
internal sealed class GitCreateBranchTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "branchName": {
                    "type": "string",
                    "description": "The name of the new branch to create."
                }
            },
            "required": ["branchName"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitCreateBranchTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_create_branch";

    public string Description => "Creates a new local Git branch from the current HEAD commit. Does not switch to the new branch; use git_checkout to switch or use git_checkout with the branchName parameter to create and switch.";

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
            await git.CreateBranchAsync(branchName, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Ok($"Created branch: {branchName}");
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