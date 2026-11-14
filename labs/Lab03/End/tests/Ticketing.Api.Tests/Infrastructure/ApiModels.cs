namespace Ticketing.Api.Tests.Infrastructure;

// The tests describe the API from the CALLER's side, with their own copies of the
// wire shapes. That is what lets the same tests prove a refactor changed nothing.
public record TicketDto(
    Guid Id, string Title, string Description, string Status, string Priority,
    string RequesterId, string? AssigneeId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record CommentDto(Guid Id, Guid TicketId, string AuthorId, string Body, DateTimeOffset CreatedAt);
