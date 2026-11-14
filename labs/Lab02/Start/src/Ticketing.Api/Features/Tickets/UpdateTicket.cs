using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;

namespace Ticketing.Api.Features.Tickets;

// Only the fields a caller is changing; nulls are left alone.
public record UpdateTicketRequest(string? Title, string? Description, Domain.TicketPriority? Priority);

public static class UpdateTicket
{
    public static RouteGroupBuilder MapUpdateTicket(this RouteGroupBuilder group)
    {
        group.MapPatch("/{id:guid}", Handle).WithName("UpdateTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, NotFound>> Handle(
        Guid id, UpdateTicketRequest request, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return TypedResults.NotFound();

        if (request.Title is not null) ticket.Title = request.Title;
        if (request.Description is not null) ticket.Description = request.Description;
        if (request.Priority is not null) ticket.Priority = request.Priority.Value;

        await store.UpdateAsync(ticket, cancellationToken);
        return TypedResults.Ok(ticket.ToResponse());
    }
}
