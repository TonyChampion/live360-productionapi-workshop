using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Tickets;

public record AssignTicketRequest(string AssigneeId);

public static class AssignTicket
{
    public static RouteGroupBuilder MapAssignTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/assign", Handle).WithName("AssignTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, NotFound>> Handle(
        Guid id, AssignTicketRequest request, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return TypedResults.NotFound();

        if (!ticket.CanBeAssigned())
            throw new InvalidOperationException($"Ticket {id} is {ticket.Status} and cannot be assigned.");

        ticket.AssigneeId = request.AssigneeId;
        ticket.Status = TicketStatus.InProgress;
        await store.UpdateAsync(ticket, cancellationToken);
        return TypedResults.Ok(ticket.ToResponse());
    }
}
