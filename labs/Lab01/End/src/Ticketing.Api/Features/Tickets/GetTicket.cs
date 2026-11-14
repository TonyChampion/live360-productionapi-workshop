using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;

namespace Ticketing.Api.Features.Tickets;

public static class GetTicket
{
    public static RouteGroupBuilder MapGetTicket(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}", Handle).WithName("GetTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, NotFound>> Handle(
        Guid id, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        return ticket is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ticket.ToResponse());
    }
}
