using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Tickets;

public static class CloseTicket
{
    public static RouteGroupBuilder MapCloseTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/close", Handle).WithName("CloseTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, NotFound>> Handle(
        Guid id, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return TypedResults.NotFound();

        if (!ticket.CanMoveTo(TicketStatus.Closed))
            throw new InvalidOperationException($"Ticket {id} is already {ticket.Status}.");

        ticket.Status = TicketStatus.Closed;
        await store.UpdateAsync(ticket, cancellationToken);
        return TypedResults.Ok(ticket.ToResponse());
    }
}
