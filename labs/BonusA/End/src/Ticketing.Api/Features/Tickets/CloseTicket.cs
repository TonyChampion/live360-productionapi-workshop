using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Features.Events;
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
        Guid id, ITicketStore store, ClaimsPrincipal user, TicketLog log, ITicketEvents events,
        CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!ticket.CanMoveTo(TicketStatus.Closed))
            return Problems.InvalidTransition(ticket, "closed");

        ticket.Status = TicketStatus.Closed;
        await store.UpdateAsync(ticket, cancellationToken);
        log.Closed(ticket.Id, user.UserId());

        // Tell anyone who cares. This slice doesn't know (or need to know) who that is.
        await events.PublishAsync(new TicketClosed(ticket.Id, user.UserId()), cancellationToken);
        return TypedResults.Ok(ticket.ToResponse());
    }
}
