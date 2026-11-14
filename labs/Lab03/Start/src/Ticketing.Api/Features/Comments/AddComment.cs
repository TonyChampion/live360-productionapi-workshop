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
        Guid ticketId, AddCommentRequest request, ITicketStore store, HttpRequest http,
        CancellationToken cancellationToken)
    {
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
