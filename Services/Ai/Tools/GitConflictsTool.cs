using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that lists files currently in merge conflict in the Git repository.
/// </summary>
internal sealed class GitConflictsTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {},
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitConflictsTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_conflicts";

    public string Description => "Lists all files with unresolved merge conflicts in the Git repository. Use git_resolve_conflict to resolve them.";

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

        var conflictedFiles = git.GetConflictedFiles();
        if (conflictedFiles.Count == 0)
        {
            return Task.FromResult(ToolCallResult.Ok("No merge conflicts found."));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {conflictedFiles.Count} conflicted file(s):");
        foreach (var file in conflictedFiles)
        {
            sb.AppendLine($"  {file}");
        }

        sb.AppendLine();
        sb.AppendLine("Use git_resolve_conflict with filePath and resolution (accept_current, accept_incoming, or accept_both) to resolve conflicts.");

        return Task.FromResult(ToolCallResult.Ok(sb.ToString().TrimEnd()));
    }
}