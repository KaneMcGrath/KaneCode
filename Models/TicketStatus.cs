namespace KaneCode.Models;

/// <summary>
/// The lifecycle status of a KaneCode ticket.
///
/// Tickets are plain text files stored under <c>.kanecode/tickets</c>. The status is
/// persisted in the first line of the file as part of the <c>KaneCodeTicket</c> header,
/// which lets external tooling read and update ticket state without the IDE running.
/// </summary>
public enum TicketStatus
{
    /// <summary>
    /// The ticket file exists but has no <c>KaneCodeTicket</c> header yet.
    /// The ticket system adds one the first time it scans the file.
    /// </summary>
    Initialize,

    /// <summary>
    /// The ticket has a <c>KaneCodeTicket</c> header that could not be parsed.
    /// The user must correct the header before the ticket can be dispatched.
    /// </summary>
    Error,

    /// <summary>
    /// The ticket requests a per-ticket provider/model/agent-mode override, but the
    /// ticket system is configured to disallow ticket-side overrides.
    /// </summary>
    Blocked,

    /// <summary>
    /// The user has manually marked the ticket to be skipped until further notice.
    /// </summary>
    Ignore,

    /// <summary>
    /// The ticket is ready to be worked on.
    /// </summary>
    Open,

    /// <summary>
    /// An agent is currently working on this ticket.
    /// </summary>
    Working,

    /// <summary>
    /// An agent is working on this ticket, but its session was paused by the user.
    /// The session remains in memory and can be resumed.
    /// </summary>
    Paused,

    /// <summary>
    /// An agent finished the ticket successfully.
    /// </summary>
    Complete,

    /// <summary>
    /// An agent determined it cannot complete the ticket without a requirement
    /// it does not have.
    /// </summary>
    Unable,

    /// <summary>
    /// An agent session exited prematurely or failed in some other way.
    /// </summary>
    Failed
}
