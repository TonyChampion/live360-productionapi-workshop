using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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

    public static async Task<Results<Ok<TicketResponse>, StatusCodeHttpResult, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, ClaimsPrincipal user, IAuthorizationService auth,
        HttpContext http, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!(await auth.AuthorizeAsync(user, ticket, Policies.TicketAccess)).Succeeded)
            return Problems.Forbidden($"You don't have access to ticket {ticket.Id}.");

        var etag = ETags.For(ticket);
        http.Response.SetETag(etag);

        // The caller already has this version: say so, and skip the body
        if (http.Request.HasCurrentVersion(etag))
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);

        return TypedResults.Ok(ticket.ToResponse());
    }
}
