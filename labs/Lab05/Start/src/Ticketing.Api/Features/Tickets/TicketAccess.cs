using Microsoft.AspNetCore.Authorization;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

/// <summary>"May this user touch this ticket?" The answer depends on the ticket, not just the user.</summary>
public class TicketAccessRequirement : IAuthorizationRequirement;

public class TicketAccessHandler : AuthorizationHandler<TicketAccessRequirement, Ticket>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TicketAccessRequirement requirement, Ticket ticket)
    {
        // Agents can touch any ticket; requesters only their own
        if (context.User.IsAgent() || ticket.RequesterId == context.User.UserId())
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
