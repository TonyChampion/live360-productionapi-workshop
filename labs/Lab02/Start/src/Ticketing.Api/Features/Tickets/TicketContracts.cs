using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Tickets;

// What a caller may send. No Id, Status, or AssigneeId: the server owns those.
public record CreateTicketRequest(
    string Title,
    string Description,
    TicketPriority Priority,
    string RequesterId);

// What a caller gets back. Comments are their own resource; the ticket just counts them.
public record TicketResponse(
    Guid Id,
    string Title,
    string Description,
    TicketStatus Status,
    TicketPriority Priority,
    string RequesterId,
    string? AssigneeId,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class TicketMappings
{
    public static TicketResponse ToResponse(this Ticket t) => new(
        t.Id, t.Title, t.Description, t.Status, t.Priority,
        t.RequesterId, t.AssigneeId, t.Comments.Count, t.CreatedAt, t.UpdatedAt);
}
