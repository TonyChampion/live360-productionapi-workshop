using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Comments;

public static class AddComment
{
    public static RouteGroupBuilder MapAddComment(this RouteGroupBuilder group)
    {
        group.MapPost("/", Handle).WithName("AddComment");
        return group;
    }

    public static async Task<Results<Created<CommentResponse>, ProblemHttpResult>> Handle(
        Guid ticketId, AddCommentRequest request, ITicketStore store, ClaimsPrincipal user,
        IAuthorizationService auth, HttpRequest http, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(ticketId, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(ticketId);

        if (!(await auth.AuthorizeAsync(user, ticket, Policies.TicketAccess)).Succeeded)
            return Problems.Forbidden($"You don't have access to ticket {ticket.Id}.");

        var comment = new Comment
        {
            AuthorId = request.AuthorId,
            Body = request.Body,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var added = await store.AddCommentAsync(ticketId, comment, cancellationToken);
        return added is null
            ? Problems.TicketNotFound(ticketId)
            : TypedResults.Created(http.LocationFor(added.Id), added.ToResponse());
    }
}
