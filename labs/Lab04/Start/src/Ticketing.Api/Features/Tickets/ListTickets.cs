using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class ListTickets
{
    public static RouteGroupBuilder MapListTickets(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListTickets");
        return group;
    }

    public static async Task<Ok<List<TicketResponse>>> Handle(
        ITicketStore store, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var tickets = await store.ListAsync(cancellationToken);

        // Agents see everything; requesters see only their own tickets
        var visible = user.IsAgent()
            ? tickets
            : tickets.Where(t => t.RequesterId == user.UserId());

        return TypedResults.Ok(visible.Select(t => t.ToResponse()).ToList());
    }
}
