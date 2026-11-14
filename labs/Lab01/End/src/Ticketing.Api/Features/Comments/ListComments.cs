using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;

namespace Ticketing.Api.Features.Comments;

public static class ListComments
{
    public static RouteGroupBuilder MapListComments(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListComments");
        return group;
    }

    public static async Task<Results<Ok<List<CommentResponse>>, NotFound>> Handle(
        Guid ticketId, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(ticketId, cancellationToken);
        return ticket is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ticket.Comments.Select(c => c.ToResponse()).ToList());
    }
}
