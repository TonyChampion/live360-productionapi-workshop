using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Jobs;

public static class GetJob
{
    public static RouteGroupBuilder MapGetJob(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}", Handle).WithName("GetJob");
        group.MapGet("/dead-letters", (JobStore jobs) => TypedResults.Ok(jobs.DeadLetters())).WithName("ListDeadLetters");
        return group;
    }

    public static Results<Ok<JobResponse>, ProblemHttpResult> Handle(Guid id, JobStore jobs, HttpResponse response)
    {
        var job = jobs.Get(id);
        if (job is null)
            return Problems.JobNotFound(id);

        // Still working: hint how soon it's worth asking again
        if (job.Status is JobStatus.Queued or JobStatus.Running)
            response.Headers.RetryAfter = "1";

        return TypedResults.Ok(job);
    }
}
