using System.Text.Json;
using KaneCode.Services.Tickets;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that marks the current ticket as successfully completed.
/// A ticket agent must call this (or <see cref="UnableToCompleteTool"/>) when it
/// finishes its work so the ticket system can advance to the next ticket.
/// </summary>
internal sealed class CompleteTicketTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "summary": {
                    "type": "string",
                    "description": "A short summary of what was accomplished."
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly Func<ITicketStatusService?> _serviceProvider;

    public CompleteTicketTool(Func<ITicketStatusService?> serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    public string Name => "complete_ticket";

    public string Description =>
        "Marks the current ticket as successfully completed. Call this exactly once, as the " +
        "final tool call, when you have finished all the work the ticket asked for. " +
        "Provide a short summary of what was accomplished.";

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

        string? summary = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("summary", out JsonElement summaryElement) &&
            summaryElement.ValueKind == JsonValueKind.String)
        {
            summary = summaryElement.GetString();
        }

        bool updated = await service.MarkTicketCompleteAsync(ticketId, summary).ConfigureAwait(false);
        return updated
            ? ToolCallResult.Ok("Ticket marked complete.")
            : ToolCallResult.Fail($"Ticket '{ticketId}' could not be found.");
    }
}
