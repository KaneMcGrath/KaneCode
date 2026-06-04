using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that lists all local Git branches and indicates the currently checked-out branch.
/// </summary>
internal sealed class GitBranchesTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {},
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitBranchesTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_branches";

    public string Description => "Lists all local Git branches and marks the currently checked-out branch.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var git = _gitServiceProvider();
        if (git is null || !git.IsRepositoryOpen)
        {
            return Task.FromResult(ToolCallResult.Fail("No Git repository is currently open."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var branches = git.GetLocalBranches();
        var current = git.CurrentBranchName ?? "(detached HEAD)";

        var sb = new StringBuilder();
        sb.AppendLine($"Current branch: {current}");
        sb.AppendLine();

        if (branches.Count == 0)
        {
            sb.AppendLine("(no branches found)");
        }
        else
        {
            foreach (var branch in branches)
            {
                string marker = string.Equals(branch, current, StringComparison.OrdinalIgnoreCase)
                    ? "* "
                    : "  ";
                sb.AppendLine($"{marker}{branch}");
            }
        }

        return Task.FromResult(ToolCallResult.Ok(sb.ToString().TrimEnd()));
    }
}