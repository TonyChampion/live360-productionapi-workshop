using Asp.Versioning;
using Ticketing.Api.Features.Comments;
using Ticketing.Api.Features.Escalations;
using Ticketing.Api.Features.Jobs;
using Ticketing.Api.Features.Tickets;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features;

public static class TicketingEndpoints
{
    /// <summary>
    /// The whole public surface of the API, in one place.
    /// </summary>
    public static IEndpointRouteBuilder MapTicketingEndpoints(this IEndpointRouteBuilder app)
    {
        // Every route lives under a version from day one, even with only v1
        var api = app.NewVersionedApi("Ticketing")
            .MapGroup("/api/v{version:apiVersion}")
            .HasApiVersion(new ApiVersion(1, 0))
            .RequireAuthorization();   // every ticket endpoint needs a signed-in user

        api.MapGroup("/tickets")
            .WithTags("Tickets")
            .MapListTickets()
            .MapGetTicket()
            .MapCreateTicket()
            .MapUpdateTicket()
            .MapAssignTicket()
            .MapCloseTicket()
            .MapBulkCloseTickets();

        api.MapGroup("/tickets/{ticketId:guid}/comments")
            .WithTags("Comments")
            .MapListComments()
            .MapAddComment();

        api.MapGroup("/jobs")
            .WithTags("Jobs")
            .RequireAuthorization(Policies.Agent)
            .MapGetJob();

        api.MapGroup("/escalations")
            .WithTags("Escalations")
            .RequireAuthorization(Policies.Agent)
            .MapListEscalations();

        return app;
    }
}
