using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that retrieves Git commit history for the current branch.
/// </summary>
internal sealed class GitLogTool : IAgentTool
{
    private const int DefaultMaxCount = 30;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "maxCount": {
                    "type": "integer",
                    "description": "Maximum number of commits to return (1-200). Defaults to 30.",
                    "minimum": 1,
                    "maximum": 200
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitLogTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_log";

    public string Description => "Returns the commit history for the current Git branch. Each entry includes the short hash, author, date, and commit message.";

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

        int maxCount = DefaultMaxCount;
        if (arguments.TryGetProperty("maxCount", out var countElement) &&
            countElement.ValueKind == JsonValueKind.Number &&
            countElement.TryGetInt32(out var customCount) &&
            customCount > 0)
        {
            maxCount = Math.Min(customCount, 200);
        }

        var commits = git.GetCommitHistory(maxCount);
        if (commits.Count == 0)
        {
            return Task.FromResult(ToolCallResult.Ok("(no commits found)"));
        }

        var sb = new StringBuilder();
        foreach (var commit in commits)
        {
            string date = commit.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            sb.AppendLine($"{commit.ShortHash}  {date}  {commit.Author}  {commit.Message}");
        }

        return Task.FromResult(ToolCallResult.Ok(sb.ToString().TrimEnd()));
    }
}