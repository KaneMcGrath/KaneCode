namespace KaneCode.Services.Tickets;

/// <summary>
/// The subset of the ticket system that ticket status tools need.
/// Implemented by <see cref="TicketSystem"/> so <c>complete_ticket</c> and
/// <c>unable_to_complete</c> can update the ticket they are running under.
/// </summary>
internal interface ITicketStatusService
{
    /// <summary>Marks a ticket complete. Returns false when the ticket is unknown.</summary>
    Task<bool> MarkTicketCompleteAsync(string ticketId, string? summary);

    /// <summary>Marks a ticket unable-to-complete. Returns false when the ticket is unknown.</summary>
    Task<bool> MarkTicketUnableAsync(string ticketId, string? summary);
}
