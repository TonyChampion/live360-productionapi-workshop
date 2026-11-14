using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public record AssignTicketRequest([Required, StringLength(100)] string AssigneeId);

public static class AssignTicket
{
    public static RouteGroupBuilder MapAssignTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/assign", Handle)
            .WithName("AssignTicket")
            .RequireAuthorization(Policies.Agent);
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, AssignTicketRequest request, ITicketStore store, ClaimsPrincipal user, TicketLog log,
        CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!ticket.CanBeAssigned())
            return Problems.InvalidTransition(ticket, "assigned");

        ticket.AssigneeId = request.AssigneeId;
        ticket.Status = TicketStatus.InProgress;
        await store.UpdateAsync(ticket, cancellationToken);
        log.Assigned(ticket.Id, request.AssigneeId, user.UserId());
        return TypedResults.Ok(ticket.ToResponse());
    }
}
