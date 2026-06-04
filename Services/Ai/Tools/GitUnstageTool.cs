using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that unstages files in Git (removes them from the index).
/// Can unstage a single file or all staged files.
/// </summary>
internal sealed class GitUnstageTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "Repository-relative path of the file to unstage. If omitted, all staged files are unstaged."
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitUnstageTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_unstage";

    public string Description => "Removes a file (or all files) from the Git staging area/index. If no filePath is provided, all staged files are unstaged. Working directory changes are preserved.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var git = _gitServiceProvider();
        if (git is null || !git.IsRepositoryOpen)
        {
            return Task.FromResult(ToolCallResult.Fail("No Git repository is currently open."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (arguments.TryGetProperty("filePath", out var fpElement) &&
                fpElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(fpElement.GetString()))
            {
                string filePath = fpElement.GetString()!.Trim();
                git.UnstageFile(filePath);
                return Task.FromResult(ToolCallResult.Ok($"Unstaged: {filePath}"));
            }
            else
            {
                git.UnstageAll();
                return Task.FromResult(ToolCallResult.Ok("Unstaged all staged files."));
            }
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
    }
}