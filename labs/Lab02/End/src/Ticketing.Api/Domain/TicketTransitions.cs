namespace Ticketing.Api.Domain;

/// <summary>
/// Which status changes are legal. The API asks this before changing a ticket.
/// </summary>
public static class TicketTransitions
{
    private static readonly Dictionary<TicketStatus, TicketStatus[]> Allowed = new()
    {
        [TicketStatus.Open]       = [TicketStatus.InProgress, TicketStatus.Closed],
        [TicketStatus.InProgress] = [TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.Resolved]   = [TicketStatus.InProgress, TicketStatus.Closed],
        [TicketStatus.Closed]     = []
    };

    public static bool CanMoveTo(this Ticket ticket, TicketStatus next) =>
        Allowed[ticket.Status].Contains(next);

    /// <summary>Assigning is allowed while a ticket is still being worked.</summary>
    public static bool CanBeAssigned(this Ticket ticket) =>
        ticket.Status is TicketStatus.Open or TicketStatus.InProgress;
}
