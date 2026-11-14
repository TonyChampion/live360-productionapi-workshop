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
    public const string JobNotFound = Base + "job-not-found";           // 404: no such job
    public const string InvalidTransition = Base + "invalid-transition"; // 409: ticket's state won't allow it
    public const string PreconditionFailed = Base + "precondition-failed"; // 412: changed since you read it
    public const string IdempotencyKeyInvalid = Base + "idempotency-key-invalid";       // 400: empty or too long
    public const string IdempotencyKeyInProgress = Base + "idempotency-key-in-progress"; // 409: first try still running
    public const string IdempotencyKeyReused = Base + "idempotency-key-reused";         // 422: same key, different request
    public const string RateLimited = Base + "rate-limited";            // 429: slow down
    public const string StoreUnavailable = Base + "store-unavailable";  // 503: a dependency is failing
    public const string Timeout = Base + "timeout";                     // 504: ran out of time
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

    public static ProblemHttpResult JobNotFound(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            type: ProblemTypes.JobNotFound,
            title: "Job not found",
            detail: $"No job with id {id} exists.");

    public static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            type: ProblemTypes.Forbidden,
            title: "Forbidden",
            detail: detail);

    public static ProblemHttpResult PreconditionFailed(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            type: ProblemTypes.PreconditionFailed,
            title: "The ticket has changed",
            detail: $"Ticket {id} was changed after you read it. Get the latest version and try again.");

    public static ProblemHttpResult InvalidTransition(Ticket ticket, string action) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: ProblemTypes.InvalidTransition,
            title: "Invalid status transition",
            detail: $"Ticket {ticket.Id} is {ticket.Status} and cannot be {action}.",
            extensions: new Dictionary<string, object?> { ["currentStatus"] = ticket.Status.ToString() });

    public static ProblemHttpResult IdempotencyKeyInvalid(int maxLength) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            type: ProblemTypes.IdempotencyKeyInvalid,
            title: "Invalid Idempotency-Key",
            detail: $"An Idempotency-Key must be 1 to {maxLength} characters. A new GUID per operation works well.");

    public static ProblemHttpResult IdempotencyKeyInProgress() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: ProblemTypes.IdempotencyKeyInProgress,
            title: "Request already in progress",
            detail: "A request with this Idempotency-Key is still being processed. Retry shortly to get its result.");

    public static ProblemHttpResult IdempotencyKeyReused() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            type: ProblemTypes.IdempotencyKeyReused,
            title: "Idempotency-Key reused",
            detail: "This Idempotency-Key was already used with a different request. Use a new key for a new operation.");
}
