namespace Ticketing.Api.Features.Tickets;

/// <summary>
/// Structured log events for ticket changes. The [LoggerMessage] source generator writes the
/// implementations: no string formatting unless the level is enabled, no boxing, and every
/// {Placeholder} becomes a named property you can query on (TicketId, UserId), not just text.
/// </summary>
public partial class TicketLog(ILogger<TicketLog> logger)
{
    private readonly ILogger _logger = logger;

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Ticket {TicketId} created by {UserId} for {RequesterId}")]
    public partial void Created(Guid ticketId, string userId, string requesterId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Ticket {TicketId} assigned to {AssigneeId} by {UserId}")]
    public partial void Assigned(Guid ticketId, string assigneeId, string userId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Ticket {TicketId} closed by {UserId}")]
    public partial void Closed(Guid ticketId, string userId);
}
