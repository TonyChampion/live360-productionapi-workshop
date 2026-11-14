using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Comments;

public record AddCommentRequest(string AuthorId, string Body);

public record CommentResponse(Guid Id, Guid TicketId, string AuthorId, string Body, DateTimeOffset CreatedAt);

public static class CommentMappings
{
    public static CommentResponse ToResponse(this Comment c) =>
        new(c.Id, c.TicketId, c.AuthorId, c.Body, c.CreatedAt);
}
