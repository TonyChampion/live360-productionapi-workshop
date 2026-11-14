using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class CloseTicket
{
    public static RouteGroupBuilder MapCloseTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/close", Handle)
            .WithName("CloseTicket")
            .RequireAuthorization(Policies.Agent);
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, ClaimsPrincipal user, TicketLog log, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!ticket.CanMoveTo(TicketStatus.Closed))
            return Problems.InvalidTransition(ticket, "closed");

        ticket.Status = TicketStatus.Closed;
        await store.UpdateAsync(ticket, cancellationToken);
        log.Closed(ticket.Id, user.UserId());
        return TypedResults.Ok(ticket.ToResponse());
    }
}
