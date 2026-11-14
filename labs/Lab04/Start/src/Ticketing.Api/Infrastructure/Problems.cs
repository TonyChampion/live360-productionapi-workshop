using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// The catalog of problem types this API can return. Each type is a stable identifier
/// a caller can code against; title and detail are for humans.
/// </summary>
public static class ProblemTypes
{
    public const string Base = "https://ticketing.example/problems/";

    public const string Validation = Base + "validation";               // 400: the request is malformed
    public const string Unauthorized = Base + "unauthorized";           // 401: no valid identity
    public const string Forbidden = Base + "forbidden";                 // 403: known identity, not allowed
    public const string TicketNotFound = Base + "ticket-not-found";     // 404: no such ticket
    public const string InvalidTransition = Base + "invalid-transition"; // 409: ticket's state won't allow it
    public const string NotFound = Base + "not-found";                  // 404: no such route
    public const string Unexpected = Base + "unexpected";               // 500: our bug, not theirs
}

/// <summary>
/// Factory methods for the problems a handler returns on purpose.
/// </summary>
public static class Problems
{
    public static ProblemHttpResult TicketNotFound(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            type: ProblemTypes.TicketNotFound,
            title: "Ticket not found",
            detail: $"No ticket with id {id} exists.");

    public static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            type: ProblemTypes.Forbidden,
            title: "Forbidden",
            detail: detail);

    public static ProblemHttpResult InvalidTransition(Ticket ticket, string action) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: ProblemTypes.InvalidTransition,
            title: "Invalid status transition",
            detail: $"Ticket {ticket.Id} is {ticket.Status} and cannot be {action}.",
            extensions: new Dictionary<string, object?> { ["currentStatus"] = ticket.Status.ToString() });
}
