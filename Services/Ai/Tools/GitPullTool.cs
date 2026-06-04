using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that pulls (fetches + merges) updates from a remote Git repository
/// into the current branch.
/// </summary>
internal sealed class GitPullTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "remote": {
                    "type": "string",
                    "description": "The remote name to pull from. Defaults to 'origin'.",
                    "default": "origin"
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitPullTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_pull";

    public string Description => "Pulls updates from a remote Git repository into the current branch. Fetches remote changes and merges them into the working tree.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

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
            await git.PullAsync(remoteName, progress, cancellationToken).ConfigureAwait(false);

            string summary = progressMessages.Count > 0
                ? string.Join(Environment.NewLine, progressMessages.TakeLast(3))
                : "(no pull details)";

            return ToolCallResult.Ok($"Pulled from remote '{remoteName}'.{Environment.NewLine}{summary}");
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