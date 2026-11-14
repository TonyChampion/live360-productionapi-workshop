using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Jobs;

public record BulkCloseRequest([Required, MinLength(1), MaxLength(100)] Guid[] TicketIds);

public static class BulkCloseTickets
{
    public static RouteGroupBuilder MapBulkCloseTickets(this RouteGroupBuilder group)
    {
        group.MapPost("/bulk-close", Handle)
            .WithName("BulkCloseTickets")
            .RequireAuthorization(Policies.Agent);
        return group;
    }

    /// <summary>
    /// Doesn't close anything. It records the job, queues it, and answers 202 Accepted with where
    /// to check on it. The store isn't touched, so this returns in milliseconds however slow it is.
    /// </summary>
    public static async Task<Accepted<JobResponse>> Handle(
        BulkCloseRequest request, ITicketJobQueue queue, JobStore jobs, ClaimsPrincipal user,
        LinkGenerator links, HttpContext http, CancellationToken cancellationToken)
    {
        var job = new TicketJob(
            Guid.NewGuid(),
            request.TicketIds.Distinct().ToArray(),
            user.UserId(),
            TraceParent: Activity.Current?.Id);   // so the work joins this request's trace

        var status = jobs.Create(job);
        await queue.EnqueueAsync(job, cancellationToken);

        var location = links.GetUriByName(http, "GetJob", new { id = job.Id });
        return TypedResults.Accepted(location, status);
    }
}
