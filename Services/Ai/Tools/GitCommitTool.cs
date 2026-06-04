using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that creates a Git commit from currently staged changes.
/// </summary>
internal sealed class GitCommitTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "message": {
                    "type": "string",
                    "description": "The commit message describing the staged changes."
                }
            },
            "required": ["message"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitCommitTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_commit";

    public string Description => "Creates a Git commit from the currently staged changes. Requires a commit message. Use git_stage first to stage changes.";

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

        if (!arguments.TryGetProperty("message", out var msgElement) ||
            string.IsNullOrWhiteSpace(msgElement.GetString()))
        {
            return ToolCallResult.Fail("Missing required parameter: message");
        }

        string message = msgElement.GetString()!.Trim();

        try
        {
            await git.CommitAsync(message, cancellationToken).ConfigureAwait(false);
            return ToolCallResult.Ok($"Committed with message: {message}");
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