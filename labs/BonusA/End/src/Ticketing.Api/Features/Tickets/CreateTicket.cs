using FluentValidation;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class CreateTicket
{
    public static RouteGroupBuilder MapCreateTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/", Handle)
            .WithName("CreateTicket")
            .WithValidator<CreateTicketRequest>()
            .RequireRateLimiting(RateLimitPolicies.CreateTicket)
            .WithIdempotency<CreateTicketRequest>();
        return group;
    }

    public static async Task<Results<Created<TicketResponse>, ProblemHttpResult>> Handle(
        CreateTicketRequest request, ITicketStore store, ClaimsPrincipal user, TicketLog log, HttpRequest http,
        [FromHeader(Name = IdempotencyHeaders.Key)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Requesters open tickets for themselves; agents can open them on anyone's behalf
        if (!user.IsAgent() && request.RequesterId != user.UserId())
            return Problems.Forbidden("You can only create tickets for yourself.");

        // With a key, the id comes from the key: a retry finds the ticket the first call made
        var id = Guid.NewGuid();
        if (idempotencyKey is not null)
        {
            id = IdempotencyKeys.IdFor(user.UserId(), idempotencyKey);
            if (await store.GetAsync(id, cancellationToken) is { } existing)
                return TypedResults.Created(http.LocationFor(existing.Id), existing.ToResponse());
        }

        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Id = id,
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            RequesterId = request.RequesterId,
            Status = TicketStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };

        await store.AddAsync(ticket, cancellationToken);
        log.Created(ticket.Id, user.UserId(), ticket.RequesterId);

        // Location built from the request URL, so it stays right when v2 arrives
        return TypedResults.Created(http.LocationFor(ticket.Id), ticket.ToResponse());
    }
}

/// <summary>
/// Rules the attributes on <see cref="CreateTicketRequest"/> can't express. Here, one field depends on another.
/// </summary>
public class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(r => r.Description)
            .MinimumLength(10)
            .When(r => r.Priority == TicketPriority.Urgent)
            .WithMessage("Urgent tickets need a description of at least 10 characters.");
    }
}
