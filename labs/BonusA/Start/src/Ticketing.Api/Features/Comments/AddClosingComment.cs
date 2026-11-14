using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Features.Events;

namespace Ticketing.Api.Features.Comments;

/// <summary>
/// The Comments slice's reaction to a ticket closing. The close slice doesn't call this, or know
/// it exists: it publishes TicketClosed, and any slice that cares subscribes.
/// </summary>
public class AddClosingComment(ITicketStore store) : ITicketEventHandler<TicketClosed>
{
    public async Task HandleAsync(TicketClosed ticketEvent, CancellationToken cancellationToken) =>
        await store.AddCommentAsync(ticketEvent.TicketId, new Comment
        {
            AuthorId = "system",
            Body = $"Ticket closed by {ticketEvent.ClosedBy}.",
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
}
