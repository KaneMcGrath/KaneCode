using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that retrieves the HEAD (committed) version of a file from Git.
/// Useful to compare the committed version against the working tree.
/// </summary>
internal sealed class GitHeadFileTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "Repository-relative path of the file to read from HEAD."
                }
            },
            "required": ["filePath"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitHeadFileTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_head_file";

    public string Description => "Returns the committed (HEAD) version of a file from the Git repository. This is the version as it exists in the latest commit, not the current working-tree version. Use this to see what the committed version looks like.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var git = _gitServiceProvider();
        if (git is null || !git.IsRepositoryOpen)
        {
            return Task.FromResult(ToolCallResult.Fail("No Git repository is currently open."));
        }

        if (!arguments.TryGetProperty("filePath", out var fpElement) ||
            string.IsNullOrWhiteSpace(fpElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string filePath = fpElement.GetString()!.Trim();

        try
        {
            string content = git.GetHeadFileText(filePath);
            if (string.IsNullOrEmpty(content))
            {
                return Task.FromResult(ToolCallResult.Ok($"(file '{filePath}' does not exist in HEAD)"));
            }

            return Task.FromResult(ToolCallResult.Ok(content));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
    }
}