using System.ComponentModel.DataAnnotations;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Comments;

public record AddCommentRequest(
    [Required, StringLength(100)] string AuthorId,
    [Required, StringLength(4000)] string Body);

public record CommentResponse(Guid Id, Guid TicketId, string AuthorId, string Body, DateTimeOffset CreatedAt);

public static class CommentMappings
{
    public static CommentResponse ToResponse(this Comment c) =>
        new(c.Id, c.TicketId, c.AuthorId, c.Body, c.CreatedAt);
}
