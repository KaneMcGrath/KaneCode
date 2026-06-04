using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that resolves merge conflict markers in a conflicted file.
/// Supports accepting the current (ours), incoming (theirs), or both sides.
/// </summary>
internal sealed class GitResolveConflictTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "Repository-relative path of the conflicted file."
                },
                "resolution": {
                    "type": "string",
                    "description": "Conflict resolution strategy: 'accept_current' (keep our changes), 'accept_incoming' (accept their changes), or 'accept_both' (include both).",
                    "enum": ["accept_current", "accept_incoming", "accept_both"]
                }
            },
            "required": ["filePath", "resolution"]
        }
        """).RootElement.Clone();

    private readonly Func<GitService?> _gitServiceProvider;

    public GitResolveConflictTool(Func<GitService?> gitServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(gitServiceProvider);
        _gitServiceProvider = gitServiceProvider;
    }

    public string Name => "git_resolve_conflict";

    public string Description => "Resolves merge conflict markers in a specified file by accepting the current (ours), incoming (theirs), or both sides of the conflict. The file is updated on disk with the resolved content.";

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

        if (!arguments.TryGetProperty("filePath", out var fpElement) ||
            string.IsNullOrWhiteSpace(fpElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        if (!arguments.TryGetProperty("resolution", out var resolutionElement) ||
            string.IsNullOrWhiteSpace(resolutionElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: resolution"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string filePath = fpElement.GetString()!.Trim();
        string resolutionStr = resolutionElement.GetString()!.Trim().ToLowerInvariant();

        GitConflictResolution resolution = resolutionStr switch
        {
            "accept_current" => GitConflictResolution.AcceptCurrent,
            "accept_incoming" => GitConflictResolution.AcceptIncoming,
            "accept_both" => GitConflictResolution.AcceptBoth,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolutionStr, "Invalid resolution. Must be 'accept_current', 'accept_incoming', or 'accept_both'.")
        };

        try
        {
            git.ResolveConflict(filePath, resolution);
            return Task.FromResult(ToolCallResult.Ok($"Resolved conflicts in '{filePath}' using '{resolutionStr}'."));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }
    }
}