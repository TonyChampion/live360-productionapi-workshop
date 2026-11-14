using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;

namespace Ticketing.Api.Features.Tickets;

public static class ListTickets
{
    public static RouteGroupBuilder MapListTickets(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListTickets");
        return group;
    }

    public static async Task<Ok<List<TicketResponse>>> Handle(
        ITicketStore store, CancellationToken cancellationToken)
    {
        var tickets = await store.ListAsync(cancellationToken);
        return TypedResults.Ok(tickets.Select(t => t.ToResponse()).ToList());
    }
}
