using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that retrieves the diff of a file between HEAD and the working tree.
/// Returns both the HEAD content and the current working-tree content for comparison.
/// </summary>
internal sealed class GitDiffTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "Repository-relative path of the file to diff."
                }
            },
            "required": ["filePath"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitDiffTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_diff";

    public string Description => "Returns the HEAD (original) and working-tree (modified) content of a file for side-by-side comparison. Use this to see what has changed in a file before staging or committing.";

    public string Category => "Git";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var git = _gitServiceProvider();
        if (git is null || !git.IsRepositoryOpen)
        {
            return ToolCallResult.Fail("No Git repository is currently open.");
        }

        if (!arguments.TryGetProperty("filePath", out var fpElement) ||
            string.IsNullOrWhiteSpace(fpElement.GetString()))
        {
            return ToolCallResult.Fail("Missing required parameter: filePath");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string filePath = fpElement.GetString()!.Trim();
            var diff = await git.GetFileDiffAsync(filePath, cancellationToken).ConfigureAwait(false);

            return ToolCallResult.Ok(
                $"[HEAD content for {diff.RelativePath}]" + Environment.NewLine +
                (string.IsNullOrEmpty(diff.OriginalText) ? "(file does not exist in HEAD)" : diff.OriginalText) +
                Environment.NewLine + Environment.NewLine +
                $"[Working-tree content for {diff.RelativePath}]" + Environment.NewLine +
                (string.IsNullOrEmpty(diff.ModifiedText) ? "(file deleted from working tree)" : diff.ModifiedText));
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