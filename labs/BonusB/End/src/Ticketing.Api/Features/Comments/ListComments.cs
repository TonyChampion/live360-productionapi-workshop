using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Comments;

public static class ListComments
{
    public static RouteGroupBuilder MapListComments(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListComments");
        return group;
    }

    public static async Task<Results<Ok<List<CommentResponse>>, ProblemHttpResult>> Handle(
        Guid ticketId, ITicketStore store, ClaimsPrincipal user, IAuthorizationService auth,
        CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(ticketId, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(ticketId);

        if (!(await auth.AuthorizeAsync(user, ticket, Policies.TicketAccess)).Succeeded)
            return Problems.Forbidden($"You don't have access to ticket {ticket.Id}.");

        return TypedResults.Ok(ticket.Comments.Select(c => c.ToResponse()).ToList());
    }
}
