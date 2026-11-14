using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class CreateTicket
{
    public static RouteGroupBuilder MapCreateTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/", Handle).WithName("CreateTicket");
        return group;
    }

    public static async Task<Created<TicketResponse>> Handle(
        CreateTicketRequest request, ITicketStore store, HttpRequest http, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            RequesterId = request.RequesterId,
            Status = TicketStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };

        await store.AddAsync(ticket, cancellationToken);

        // Location built from the request URL, so it stays right when v2 arrives
        return TypedResults.Created(http.LocationFor(ticket.Id), ticket.ToResponse());
    }
}
