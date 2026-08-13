using System.Text.Json;
using KaneCode.Services.Tickets;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that marks the current ticket as unable-to-complete.
/// A ticket agent should call this when it determines the ticket cannot be finished
/// without a requirement it does not have (permissions, missing dependency, ambiguity, etc.).
/// </summary>
internal sealed class UnableToCompleteTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "reason": {
                    "type": "string",
                    "description": "A short explanation of why the ticket cannot be completed and what would be needed to unblock it."
                }
            },
            "required": ["reason"]
        }
        """).RootElement.Clone();

    private readonly Func<ITicketStatusService?> _serviceProvider;

    public UnableToCompleteTool(Func<ITicketStatusService?> serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    public string Name => "unable_to_complete";

    public string Description =>
        "Marks the current ticket as unable-to-complete. Call this when you determine the " +
        "ticket cannot be finished without a requirement you do not have (for example a missing " +
        "dependency, missing credentials, an impossible requirement, or instructions that are too " +
        "ambiguous to proceed). Provide a clear reason describing what would unblock the work.";

    public string Category => "Tickets";

    public JsonElement ParametersSchema => Schema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? ticketId = AgentToolContext.GetCurrentTicketId();
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return ToolCallResult.Fail("This tool can only be used while working on a KaneCode ticket.");
        }

        ITicketStatusService? service = _serviceProvider();
        if (service is null)
        {
            return ToolCallResult.Fail("The ticket system is not available.");
        }

        string? reason = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("reason", out JsonElement reasonElement) &&
            reasonElement.ValueKind == JsonValueKind.String)
        {
            reason = reasonElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ToolCallResult.Fail("Missing required parameter: reason");
        }

        bool updated = await service.MarkTicketUnableAsync(ticketId, reason).ConfigureAwait(false);
        return updated
            ? ToolCallResult.Ok("Ticket marked unable-to-complete.")
            : ToolCallResult.Fail($"Ticket '{ticketId}' could not be found.");
    }
}
