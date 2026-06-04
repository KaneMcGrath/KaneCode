using System.Text;
using System.Text.Json;
using KaneCode.Models;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that returns the current Git working-tree status
/// (staged, unstaged, and untracked changes).
/// </summary>
internal sealed class GitStatusTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {},
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<Services.GitService?> _gitServiceProvider;

    public GitStatusTool(Func<Services.GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_status";

    public string Description => "Returns the current Git repository working-tree status, including staged, unstaged, and untracked changes, the current branch name, and any merge conflicts.";

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

        var status = git.GetStatus();
        var branch = git.CurrentBranchName ?? "(detached HEAD)";
        var conflicted = git.GetConflictedFiles();

        var sb = new StringBuilder();
        sb.AppendLine($"Branch: {branch}");

        if (status.Count == 0)
        {
            sb.AppendLine("Working tree is clean.");
            return Task.FromResult(ToolCallResult.Ok(sb.ToString().TrimEnd()));
        }

        if (conflicted.Count > 0)
        {
            sb.AppendLine($"Conflicts: {conflicted.Count} file(s)");
            foreach (var c in conflicted)
            {
                sb.AppendLine($"  ! {c}");
            }

            sb.AppendLine();
        }

        foreach (var entry in status)
        {
            string label = GetStatusLabel(entry.Status);
            sb.AppendLine($"  {label}  {entry.FilePath}");
        }

        return Task.FromResult(ToolCallResult.Ok(sb.ToString().TrimEnd()));
    }

    private static string GetStatusLabel(LibGit2Sharp.FileStatus status)
    {
        return status switch
        {
            LibGit2Sharp.FileStatus.NewInWorkdir => "?? (untracked)",
            LibGit2Sharp.FileStatus.NewInIndex => "A  (staged)",
            LibGit2Sharp.FileStatus.ModifiedInIndex => "M  (staged)",
            LibGit2Sharp.FileStatus.DeletedFromIndex => "D  (staged)",
            LibGit2Sharp.FileStatus.RenamedInIndex => "R  (staged)",
            LibGit2Sharp.FileStatus.ModifiedInWorkdir => " M (modified, unstaged)",
            LibGit2Sharp.FileStatus.DeletedFromWorkdir => " D (deleted, unstaged)",
            LibGit2Sharp.FileStatus.Conflicted => " C (conflict)",
            _ => $" ?  ({status})"
        };
    }
}