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
        Guid ticketId, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(ticketId, cancellationToken);
        return ticket is null
            ? Problems.TicketNotFound(ticketId)
            : TypedResults.Ok(ticket.Comments.Select(c => c.ToResponse()).ToList());
    }
}
