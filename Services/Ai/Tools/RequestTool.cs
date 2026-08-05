using System.IO;
using System.Text;
using System.Text.Json;
using KaneCode.Services;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that lets an advanced agent submit a request for a tool it expects
/// to need but that is not currently available. The request is saved as a text
/// file under the KaneCode settings directory (<c>ai-tool-requests</c> folder) so
/// that an inventory of requests can be built up during agent usage and used to
/// drive future improvements to the application.
/// </summary>
/// <remarks>
/// Submitting a request has <b>no immediate effect</b> — the requested tool will
/// not become available in the current session. The agent should continue
/// finishing its task with the tools it already has.
/// </remarks>
internal sealed class RequestTool : IAgentTool
{
    private const string RequestsDirectoryName = "ai-tool-requests";

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "toolName": {
                    "type": "string",
                    "description": "The name of the tool being requested (e.g. 'update_json')."
                },
                "description": {
                    "type": "string",
                    "description": "What the requested tool should do and why it is needed to complete the task."
                },
                "reasoning": {
                    "type": "string",
                    "description": "Optional additional context on how the requested tool would be used."
                }
            },
            "required": ["toolName", "description"]
        }
        """).RootElement.Clone();

    private readonly string _requestsDirectory;

    /// <summary>
    /// Creates the tool, saving requests under the KaneCode settings directory
    /// (<c>PortablePathProvider.BaseDirectory\ai-tool-requests</c>).
    /// </summary>
    public RequestTool()
        : this(Path.Combine(PortablePathProvider.BaseDirectory, RequestsDirectoryName))
    {
    }

    /// <summary>
    /// Creates the tool with a custom requests directory (used by tests).
    /// </summary>
    internal RequestTool(string requestsDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestsDirectory))
        {
            throw new ArgumentException("The requests directory cannot be blank.", nameof(requestsDirectory));
        }

        _requestsDirectory = requestsDirectory;
    }

    public string Name => "request_tool";

    public string Category => "Debug";

    public string Description =>
        "Submit a request for a new agent tool that you expect to need but that is not " +
        "currently available. Use this when you feel limited in completing a task because " +
        "you are missing a capability you expect to exist. The request is recorded as a " +
        "text file in the KaneCode 'ai-tool-requests' folder so it can be reviewed later " +
        "to build an inventory of requested improvements. IMPORTANT: this has no immediate " +
        "effect — the requested tool will not become available in this session, and it will " +
        "only improve future versions of the application. After submitting the request, " +
        "continue finishing your task with the tools you already have.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => false;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("toolName", out JsonElement toolNameElement) ||
            string.IsNullOrWhiteSpace(toolNameElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: toolName"));
        }

        if (!arguments.TryGetProperty("description", out JsonElement descriptionElement) ||
            string.IsNullOrWhiteSpace(descriptionElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: description"));
        }

        string toolName = toolNameElement.GetString()!.Trim();
        string description = descriptionElement.GetString()!.Trim();

        string reasoning = arguments.TryGetProperty("reasoning", out JsonElement reasoningElement)
            ? (reasoningElement.GetString() ?? string.Empty).Trim()
            : string.Empty;

        try
        {
            Directory.CreateDirectory(_requestsDirectory);

            string filePath = Path.Combine(_requestsDirectory, BuildRequestFileName(toolName));
            File.WriteAllText(filePath, BuildRequestContents(toolName, description, reasoning));

            return Task.FromResult(ToolCallResult.Ok(
                $"Tool request for '{toolName}' recorded at '{filePath}'. " +
                "This has no immediate effect on the current session; it will only improve " +
                "future versions of KaneCode. Continue finishing your task with the tools you already have."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail($"Failed to save tool request: {ex.Message}"));
        }
    }

    private static string BuildRequestFileName(string toolName)
    {
        string safeToolName = SanitizeFileNamePart(toolName);
        string timestamp = DateTimeOffset.Now.ToLocalTime().ToString("yyyyMMdd-HHmmssfff");
        return $"tool-request-{timestamp}-{safeToolName}-{Guid.NewGuid():N}.txt";
    }

    private static string BuildRequestContents(string toolName, string description, string reasoning)
    {
        StringBuilder builder = new();
        builder.AppendLine("KaneCode AI tool request");
        builder.AppendLine("========================");
        builder.AppendLine();
        builder.AppendLine("Submitted by an AI agent through the 'request_tool' agent tool.");
        builder.AppendLine("This request has NO immediate effect on the current session. It is");
        builder.AppendLine("recorded to build an inventory of tool requests that can be used to");
        builder.AppendLine("improve future versions of the application.");
        builder.AppendLine();
        builder.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Requested tool: {toolName}");
        builder.AppendLine();
        builder.AppendLine("Description:");
        builder.AppendLine(description);
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            builder.AppendLine("Reasoning:");
            builder.AppendLine(reasoning);
            builder.AppendLine();
        }

        builder.AppendLine("Status: pending review for a future version of KaneCode.");
        return builder.ToString();
    }

    private static string SanitizeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tool";
        }

        StringBuilder builder = new(value.Length);

        foreach (char character in value.Trim())
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0)
            {
                builder.Append('-');
                continue;
            }

            builder.Append(char.IsWhiteSpace(character) ? '-' : character);
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }
}
