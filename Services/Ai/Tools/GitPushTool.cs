using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that pushes the current branch to a remote Git repository.
/// </summary>
internal sealed class GitPushTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "remote": {
                    "type": "string",
                    "description": "The remote name to push to. Defaults to 'origin'.",
                    "default": "origin"
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitPushTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_push";

    public string Description => "Pushes commits from the current local branch to a remote Git repository. Requires the remote to have been configured (typically via 'git remote add').";

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

        cancellationToken.ThrowIfCancellationRequested();

        string remoteName = "origin";
        if (arguments.TryGetProperty("remote", out var remoteElement) &&
            remoteElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(remoteElement.GetString()))
        {
            remoteName = remoteElement.GetString()!.Trim();
        }

        try
        {
            var progressMessages = new List<string>();
            var progress = new Progress<string>(msg => progressMessages.Add(msg));
            await git.PushAsync(remoteName, progress, cancellationToken).ConfigureAwait(false);

            string summary = progressMessages.Count > 0
                ? string.Join(Environment.NewLine, progressMessages.TakeLast(3))
                : "(no push details)";

            string branchName = git.CurrentBranchName ?? "(unknown)";
            return ToolCallResult.Ok($"Pushed '{branchName}' to remote '{remoteName}'.{Environment.NewLine}{summary}");
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