using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class GetTicket
{
    public static RouteGroupBuilder MapGetTicket(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}", Handle).WithName("GetTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        return ticket is null
            ? Problems.TicketNotFound(id)
            : TypedResults.Ok(ticket.ToResponse());
    }
}
