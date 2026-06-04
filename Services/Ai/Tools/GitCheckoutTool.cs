using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that checks out (switches to) an existing local Git branch.
/// </summary>
internal sealed class GitCheckoutTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "branchName": {
                    "type": "string",
                    "description": "The name of the branch to switch to."
                }
            },
            "required": ["branchName"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitCheckoutTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_checkout";

    public string Description => "Switches to (checks out) an existing local Git branch. The working tree is updated to match the branch tip.";

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
            await git.CheckoutAsync(branchName, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Ok($"Switched to branch: {branchName}");
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