# Lab 8: Move the Work, Keep the Contract

**Module 8: Background Processing** | about 70 to 80 minutes

Some work doesn't belong inside a request. Closing 20 tickets against a store that answers in 800 ms takes 48 seconds: three store calls each, ten times Lab 7's request budget. In this lab you'll let slices react to each other through events, accept slow work with **202 Accepted** and a place to check on it, do the work in a background worker that retries and dead-letters, and run work on a timer.

**You're done when** closing a ticket raises an event another slice handles, bulk close returns 202 in milliseconds while a worker does the work, failed jobs retry from where they stopped and dead-letter when they can't, overdue tickets are escalated on a schedule, and `dotnet test` is fully green.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/Lab08/Start` and run this lab's tests:

```
dotnet test --filter "Lab=8"
```

**All 14 fail.** Every endpoint this lab adds is new.

### What's new in your Start folder

- **`Features/Jobs/ITicketJobQueue.cs`**: the contract between "accept the work" and "do the work," and `TicketJob`, the message that crosses it.
- **`Features/Jobs/ServiceBusTicketJobQueue.cs`**: the same contract on Azure Service Bus. It compiles but isn't registered, so nothing needs Azure. You'll write the in-memory version against the same interface, and compare.
- **A "Bulk close 5 tickets" run case** in the lab client: creates five tickets, bulk closes them, and polls the job.
- **`http/08-background.http`** covers every step.

---

## Step 1: Slices talk through events (10 min)

When a ticket closes, the Comments slice wants to add a note saying who closed it. The tempting move is for `CloseTicket` to call into the Comments code. Then the close slice knows about comments, and the next feature (notify the requester, update a report) adds another call, until closing a ticket knows about everything.

Instead, the close slice announces **what happened**, and whoever cares subscribes. Create `src/Ticketing.Api/Features/Events/TicketEvents.cs`:

```csharp
namespace Ticketing.Api.Features.Events;

/// <summary>Something that happened to a ticket, that other slices may care about.</summary>
public interface ITicketEvent;

public record TicketClosed(Guid TicketId, string ClosedBy) : ITicketEvent;

/// <summary>Reacts to one kind of event. A slice that cares implements this; the publisher never knows it exists.</summary>
public interface ITicketEventHandler<in TEvent> where TEvent : ITicketEvent
{
    Task HandleAsync(TEvent ticketEvent, CancellationToken cancellationToken);
}

public interface ITicketEvents
{
    Task PublishAsync<TEvent>(TEvent ticketEvent, CancellationToken cancellationToken) where TEvent : ITicketEvent;
}

/// <summary>
/// Runs every handler for an event, in-process, before PublishAsync returns.
/// Simple and fast, and not durable: if the process dies mid-publish, the event is gone.
/// </summary>
public class InProcessTicketEvents(IServiceProvider services) : ITicketEvents
{
    public async Task PublishAsync<TEvent>(TEvent ticketEvent, CancellationToken cancellationToken) where TEvent : ITicketEvent
    {
        foreach (var handler in services.GetServices<ITicketEventHandler<TEvent>>())
            await handler.HandleAsync(ticketEvent, cancellationToken);
    }
}

public static class TicketEventsExtensions
{
    public static IServiceCollection AddTicketEvents(this IServiceCollection services)
    {
        // Scoped: handlers may depend on per-request (or per-job) services
        services.AddScoped<ITicketEvents, InProcessTicketEvents>();
        services.AddScoped<ITicketEventHandler<TicketClosed>, Comments.AddClosingComment>();
        return services;
    }
}
```

The Comments slice subscribes. Create `src/Ticketing.Api/Features/Comments/AddClosingComment.cs`:

```csharp
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Features.Events;

namespace Ticketing.Api.Features.Comments;

/// <summary>
/// The Comments slice's reaction to a ticket closing. The close slice doesn't call this, or know
/// it exists: it publishes TicketClosed, and any slice that cares subscribes.
/// </summary>
public class AddClosingComment(ITicketStore store) : ITicketEventHandler<TicketClosed>
{
    public async Task HandleAsync(TicketClosed ticketEvent, CancellationToken cancellationToken) =>
        await store.AddCommentAsync(ticketEvent.TicketId, new Comment
        {
            AuthorId = "system",
            Body = $"Ticket closed by {ticketEvent.ClosedBy}.",
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
}
```

Publish from `Features/Tickets/CloseTicket.cs`, after the ticket is saved:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Features.Events;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class CloseTicket
{
    public static RouteGroupBuilder MapCloseTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/close", Handle)
            .WithName("CloseTicket")
            .RequireAuthorization(Policies.Agent);
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, ClaimsPrincipal user, TicketLog log, ITicketEvents events,
        CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!ticket.CanMoveTo(TicketStatus.Closed))
            return Problems.InvalidTransition(ticket, "closed");

        ticket.Status = TicketStatus.Closed;
        await store.UpdateAsync(ticket, cancellationToken);
        log.Closed(ticket.Id, user.UserId());

        // Tell anyone who cares. This slice doesn't know (or need to know) who that is.
        await events.PublishAsync(new TicketClosed(ticket.Id, user.UserId()), cancellationToken);
        return TypedResults.Ok(ticket.ToResponse());
    }
}
```

Register it in `Program.cs`, after `AddTicketingResilience()`:

```csharp
builder.Services.AddTicketEvents();
```

### Checkpoint

**1 passing.** Close a ticket from `08-background.http`, then read its comments.

---

## Step 2: Accept the work (10 min)

Bulk close shouldn't close anything during the request. It should **record** the job, **queue** it, and answer **202 Accepted** with a `Location` the caller can check. That way the response takes milliseconds no matter how slow the store is.

First the in-memory queue. Create `src/Ticketing.Api/Features/Jobs/ChannelTicketJobQueue.cs`:

```csharp
using System.Threading.Channels;

namespace Ticketing.Api.Features.Jobs;

/// <summary>
/// The in-memory queue: a bounded channel. Bounded, so a flood of jobs pushes back on the callers
/// queuing them instead of quietly eating memory. In-memory, so jobs don't survive a restart.
/// </summary>
public class ChannelTicketJobQueue : ITicketJobQueue
{
    private readonly Channel<TicketJob> _channel = Channel.CreateBounded<TicketJob>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(TicketJob job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<TicketJob> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
```

A job needs a status the caller can follow. Create `src/Ticketing.Api/Features/Jobs/JobStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace Ticketing.Api.Features.Jobs;

public enum JobStatus { Queued, Running, Succeeded, DeadLettered }

/// <param name="Outcome">closed, already-closed, or not-found</param>
public record JobItemResult(Guid TicketId, string Outcome);

public record JobResponse(
    Guid Id, string Kind, JobStatus Status, int Total, int Completed, int Attempts, string? LastError,
    IReadOnlyList<JobItemResult> Results, string? TraceId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What we know about one job. Every change goes through JobStore, under a lock.</summary>
public class JobState
{
    public required Guid Id { get; init; }
    public required int Total { get; init; }
    public string? TraceId { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public List<JobItemResult> Results { get; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Job status, kept in memory so callers can follow along.</summary>
public class JobStore
{
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    public JobResponse Create(TicketJob job)
    {
        var state = new JobState { Id = job.Id, Total = job.TicketIds.Length, TraceId = job.TraceParent };
        _jobs[job.Id] = state;
        return Snapshot(state);
    }

    public JobResponse? Get(Guid id) => _jobs.TryGetValue(id, out var state) ? Snapshot(state) : null;

    public IReadOnlyList<JobResponse> DeadLetters() =>
        _jobs.Values.Where(j => j.Status == JobStatus.DeadLettered).Select(Snapshot).OrderBy(j => j.CreatedAt).ToList();

    public void Update(Guid id, Action<JobState> change)
    {
        if (!_jobs.TryGetValue(id, out var state)) return;
        lock (state)
        {
            change(state);
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public bool HasResult(Guid jobId, Guid ticketId)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return false;
        lock (state) return state.Results.Any(r => r.TicketId == ticketId);
    }

    private static JobResponse Snapshot(JobState s)
    {
        lock (s)
            return new JobResponse(s.Id, "bulk-close", s.Status, s.Total, s.Results.Count, s.Attempts, s.LastError,
                s.Results.ToList(), s.TraceId, s.CreatedAt, s.UpdatedAt);
    }
}
```

The endpoint. Create `src/Ticketing.Api/Features/Jobs/BulkCloseTickets.cs`:

```csharp
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
```

`LinkGenerator` builds the `Location` from the route **name**, so it stays right if the URL ever changes.

And the status endpoint. Create `src/Ticketing.Api/Features/Jobs/GetJob.cs`:

```csharp
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
```

Add a problem type to `ProblemTypes` in `Infrastructure/Problems.cs`:

```csharp
    public const string JobNotFound = Base + "job-not-found";           // 404: no such job
```

And its factory to `Problems`:

```csharp
    public static ProblemHttpResult JobNotFound(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            type: ProblemTypes.JobNotFound,
            title: "Job not found",
            detail: $"No job with id {id} exists.");
```

Register what you have so far. Create `src/Ticketing.Api/Features/Jobs/JobsExtensions.cs`:

```csharp
namespace Ticketing.Api.Features.Jobs;

public static class JobsExtensions
{
    public static IServiceCollection AddTicketJobs(this IServiceCollection services)
    {
        services.AddSingleton<ITicketJobQueue, ChannelTicketJobQueue>();
        services.AddSingleton<JobStore>();

        return services;
    }
}
```

Map the endpoints in `Features/TicketingEndpoints.cs`. Add `.MapBulkCloseTickets()` to the tickets group, after `.MapCloseTicket()`, and a jobs group for agents:

```csharp
        api.MapGroup("/jobs")
            .WithTags("Jobs")
            .RequireAuthorization(Policies.Agent)
            .MapGetJob();
```

(You'll need `using Ticketing.Api.Features.Jobs;` and `using Ticketing.Api.Infrastructure;`.)

In `Program.cs`, after `AddTicketEvents()`:

```csharp
builder.Services.AddTicketJobs();
```

### Checkpoint

**6 passing.** Bulk close returns 202 with a `Location`. Follow it: the job says `Queued`, forever. Nothing is doing the work yet.

---

## Step 3: Do the work in the background (10 min)

The processor does one job's worth of work. Create `src/Ticketing.Api/Features/Jobs/BulkCloseProcessor.cs`:

```csharp
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Features.Events;
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Features.Jobs;

/// <summary>
/// Does the work for one attempt of a bulk close job. Scoped: it lives for one job, like a request,
/// because ITicketEvents and its handlers are scoped.
/// </summary>
public class BulkCloseProcessor(ITicketStore store, ITicketEvents events, JobStore jobs, TicketLog log)
{
    public async Task RunAsync(TicketJob job, CancellationToken cancellationToken)
    {
        foreach (var id in job.TicketIds)
        {
            var ticket = await store.GetAsync(id, cancellationToken);
            string outcome;

            if (ticket is null)
                outcome = "not-found";
            else if (!ticket.CanMoveTo(TicketStatus.Closed))
                outcome = "already-closed";
            else
            {
                ticket.Status = TicketStatus.Closed;
                await store.UpdateAsync(ticket, cancellationToken);
                log.Closed(ticket.Id, job.RequestedBy);
                await events.PublishAsync(new TicketClosed(ticket.Id, job.RequestedBy), cancellationToken);
                outcome = "closed";
            }

            jobs.Update(job.Id, s => s.Results.Add(new JobItemResult(id, outcome)));
        }
    }
}
```

It publishes the same `TicketClosed` event as the single close, so the Comments slice reacts the same way, whichever path closed the ticket.

Now the worker. It's a **hosted service**: started with the app, stopped with it, and running for its whole life. Create `src/Ticketing.Api/Features/Jobs/TicketJobWorker.cs`:

```csharp
using System.Diagnostics;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Ticketing.Api.Chaos;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Jobs;

/// <summary>
/// Takes jobs off the queue, one at a time, for as long as the app runs. A hosted service is a
/// singleton, so anything scoped (the processor, the event handlers) comes from a fresh scope per job.
/// </summary>
public class TicketJobWorker(
    ITicketJobQueue queue,
    JobStore jobs,
    IServiceScopeFactory scopes,
    ILogger<TicketJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
            await ProcessAsync(job, stoppingToken);
    }

    private async Task ProcessAsync(TicketJob job, CancellationToken stoppingToken)
    {
        // Continue the trace of the request that queued this job
        using var span = Telemetry.Source.StartActivity("job bulk-close", ActivityKind.Consumer, job.TraceParent);
        span?.SetTag("job.id", job.Id.ToString());
        span?.SetTag("job.attempt", job.Attempt);

        jobs.Update(job.Id, s => { s.Status = JobStatus.Running; s.Attempts = job.Attempt; });

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<BulkCloseProcessor>();
            await processor.RunAsync(job, stoppingToken);

            jobs.Update(job.Id, s => s.Status = JobStatus.Succeeded);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Never let one job's failure escape: an unhandled exception here stops the whole app
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            jobs.Update(job.Id, s => { s.Status = JobStatus.DeadLettered; s.LastError = Describe(ex); });
        }
    }

    /// <summary>What went wrong, for the job's status. Never exception details: callers can read it.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        BrokenCircuitException => "The ticket store is failing; calls to it are paused.",
        StoreUnavailableException or TransientStoreException => "The ticket store is unavailable.",
        TimeoutRejectedException => "The ticket store didn't respond in time.",
        _ => "An unexpected error occurred. It has been logged."
    };
}
```

Three lessons from earlier labs meet here:

- **Lab 1, captive dependencies.** A hosted service is a singleton. `BulkCloseProcessor` needs `ITicketEvents`, which is scoped. So the worker creates a **scope per job**, the way ASP.NET Core creates one per request.
- **Lab 6, broken traces.** A queue is a hand-off, and hand-offs lose the trace. The job carries the request's trace context (`TraceParent`), and the worker continues it: the POST and all of its background work show up as **one trace**.
- **Lab 7, never fail silently.** The `catch` is not optional. Since .NET 6, an unhandled exception in a background service **stops the whole app**.

Register both in `AddTicketJobs()`:

```csharp
        services.AddScoped<BulkCloseProcessor>();
        services.AddHostedService<TicketJobWorker>();
```

### Checkpoint

**10 passing.** Make the store slow (**Slow (800 ms)** in the chaos panel) and run **Bulk close 5 tickets**: 202 in a few milliseconds, then the job counts up to 5/5 over about 12 seconds. Open its trace in the panel: the request, then the job, then every store call.

### Try it: inject the scoped service directly (optional, 2 min)

Add `ITicketEvents events` to `TicketJobWorker`'s constructor and run the API. It refuses to start:

```
Cannot consume scoped service 'Ticketing.Api.Features.Events.ITicketEvents' from singleton 'Microsoft.Extensions.Hosting.IHostedService'.
```

That's Lab 1's Experiment A, for real. Take it back out.

---

## Step 4: Retry, resume, and dead-letter (10 min)

Take the store down and run the bulk close again: the job goes straight to `DeadLettered`. One store blip shouldn't cost a whole job, but a job that can never succeed shouldn't retry forever either.

Replace `TicketJobWorker.cs` with the version that retries with exponential backoff, then dead-letters:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Ticketing.Api.Chaos;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Jobs;

/// <summary>Bound from the "Jobs" section.</summary>
public class JobOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
}

/// <summary>
/// Takes jobs off the queue, one at a time, for as long as the app runs. A hosted service is a
/// singleton, so anything scoped (the processor, the event handlers) comes from a fresh scope per job.
/// </summary>
public class TicketJobWorker(
    ITicketJobQueue queue,
    JobStore jobs,
    IServiceScopeFactory scopes,
    IOptions<JobOptions> options,
    ILogger<TicketJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.DequeueAllAsync(stoppingToken))
            await ProcessAsync(job, stoppingToken);
    }

    private async Task ProcessAsync(TicketJob job, CancellationToken stoppingToken)
    {
        // Continue the trace of the request that queued this job
        using var span = Telemetry.Source.StartActivity("job bulk-close", ActivityKind.Consumer, job.TraceParent);
        span?.SetTag("job.id", job.Id.ToString());
        span?.SetTag("job.attempt", job.Attempt);

        jobs.Update(job.Id, s => { s.Status = JobStatus.Running; s.Attempts = job.Attempt; });

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<BulkCloseProcessor>();
            await processor.RunAsync(job, stoppingToken);

            jobs.Update(job.Id, s => s.Status = JobStatus.Succeeded);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Never let one job's failure escape: an unhandled exception here stops the whole app
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var error = Describe(ex);

            if (job.Attempt < options.Value.MaxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(options.Value.RetryDelayMs * Math.Pow(2, job.Attempt - 1));
                logger.LogWarning("Job {JobId} attempt {Attempt} failed ({Error}); retrying in {DelayMs} ms",
                    job.Id, job.Attempt, error, delay.TotalMilliseconds);
                jobs.Update(job.Id, s => { s.Status = JobStatus.Queued; s.LastError = error; });
                _ = RetryLaterAsync(job with { Attempt = job.Attempt + 1 }, delay, stoppingToken);
            }
            else
            {
                logger.LogError(ex, "Job {JobId} dead-lettered after {Attempts} attempts", job.Id, job.Attempt);
                jobs.Update(job.Id, s => { s.Status = JobStatus.DeadLettered; s.LastError = error; });
            }
        }
    }

    /// <summary>Re-queue after a delay, without holding up the jobs behind this one.</summary>
    private async Task RetryLaterAsync(TicketJob job, TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            await queue.EnqueueAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) { }   // shutting down
    }

    /// <summary>What went wrong, for the job's status. Never exception details: callers can read it.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        BrokenCircuitException => "The ticket store is failing; calls to it are paused.",
        StoreUnavailableException or TransientStoreException => "The ticket store is unavailable.",
        TimeoutRejectedException => "The ticket store didn't respond in time.",
        _ => "An unexpected error occurred. It has been logged."
    };
}
```

Bind its options in `AddTicketJobs()`:

```csharp
        services.AddOptions<JobOptions>().BindConfiguration("Jobs");
```

And add them to `appsettings.json`:

```json
  "Jobs": {
    "MaxAttempts": 3,
    "RetryDelayMs": 1000
  }
```

### Checkpoint: one test is still red

**13 passing.** `A_retried_job_resumes_instead_of_closing_tickets_twice` fails. A job that died after closing the first ticket starts over from the top when it retries: it re-checks the first ticket and reports it twice. With a less careful processor (or a store without the "already closed" check), it would close it twice and announce it twice.

The fix: before doing an item, check whether a previous attempt already did it. Here's the finished `BulkCloseProcessor.cs`:

```csharp
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Features.Events;
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Features.Jobs;

/// <summary>
/// Does the work for one attempt of a bulk close job. Scoped: it lives for one job, like a request,
/// because ITicketEvents and its handlers are scoped.
/// </summary>
public class BulkCloseProcessor(ITicketStore store, ITicketEvents events, JobStore jobs, TicketLog log)
{
    public async Task RunAsync(TicketJob job, CancellationToken cancellationToken)
    {
        foreach (var id in job.TicketIds)
        {
            // A retry resumes where the last attempt stopped, so no ticket is closed (or announced) twice
            if (jobs.HasResult(job.Id, id))
                continue;

            var ticket = await store.GetAsync(id, cancellationToken);
            string outcome;

            if (ticket is null)
                outcome = "not-found";
            else if (!ticket.CanMoveTo(TicketStatus.Closed))
                outcome = "already-closed";
            else
            {
                ticket.Status = TicketStatus.Closed;
                await store.UpdateAsync(ticket, cancellationToken);
                log.Closed(ticket.Id, job.RequestedBy);
                await events.PublishAsync(new TicketClosed(ticket.Id, job.RequestedBy), cancellationToken);
                outcome = "closed";
            }

            jobs.Update(job.Id, s => s.Results.Add(new JobItemResult(id, outcome)));
        }
    }
}
```

Work that can be retried has to be safe to repeat. This is the same idea as Lab 7's "never retry a timed-out write," one level up.

---

## Step 5: Work on a timer (5 min)

Not all background work comes from a request. An urgent ticket that nobody has picked up within an hour should be escalated, whether or not anyone calls the API. Create `src/Ticketing.Api/Features/Escalations/SlaMonitor.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Escalations;

/// <summary>Bound from the "Sla" section.</summary>
public class SlaOptions
{
    /// <summary>How long an urgent or high ticket may wait for someone to pick it up.</summary>
    public int FirstResponseMinutes { get; set; } = 60;
    public int ScanIntervalMs { get; set; } = 30_000;
}

public record Escalation(Guid TicketId, string Title, TicketPriority Priority, DateTimeOffset EscalatedAt);

public class EscalationLog
{
    private readonly ConcurrentDictionary<Guid, Escalation> _escalations = new();

    /// <summary>False if the ticket was already escalated: each ticket is escalated once.</summary>
    public bool TryAdd(Escalation escalation) => _escalations.TryAdd(escalation.TicketId, escalation);

    public IReadOnlyList<Escalation> All() => _escalations.Values.OrderBy(e => e.EscalatedAt).ToList();
}

/// <summary>
/// Work on a schedule instead of on a request: every interval, find urgent and high tickets
/// that are still open and unassigned past their first-response window, and escalate them.
/// </summary>
public class SlaMonitor(ITicketStore store, EscalationLog escalations, IOptions<SlaOptions> options,
    ILogger<SlaMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.ScanIntervalMs));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // An unhandled exception in a BackgroundService stops the whole app (since .NET 6).
                // A failed scan isn't worth that: log it and try again next interval.
                logger.LogWarning(ex, "SLA scan failed; will try again in {IntervalMs} ms", options.Value.ScanIntervalMs);
            }
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-options.Value.FirstResponseMinutes);
        var tickets = await store.ListAsync(cancellationToken);

        var overdue = tickets.Where(t =>
            t.Status == TicketStatus.Open &&
            t.AssigneeId is null &&
            t.Priority >= TicketPriority.High &&
            t.CreatedAt < cutoff);

        foreach (var ticket in overdue)
            if (escalations.TryAdd(new Escalation(ticket.Id, ticket.Title, ticket.Priority, DateTimeOffset.UtcNow)))
                logger.LogWarning("Ticket {TicketId} ({Priority}) escalated: no one has picked it up", ticket.Id, ticket.Priority);
    }
}

public static class ListEscalations
{
    public static RouteGroupBuilder MapListEscalations(this RouteGroupBuilder group)
    {
        group.MapGet("/", (EscalationLog log) => TypedResults.Ok(log.All())).WithName("ListEscalations");
        return group;
    }
}
```

`PeriodicTimer` is the modern way to do this: async, cancellable, and it never overlaps (the next tick waits until the previous scan finishes). This service only needs singletons, so unlike the job worker, it injects the store directly: no scope needed.

Register it. Here's the finished `JobsExtensions.cs`:

```csharp
using Ticketing.Api.Features.Escalations;

namespace Ticketing.Api.Features.Jobs;

public static class JobsExtensions
{
    public static IServiceCollection AddTicketJobs(this IServiceCollection services)
    {
        services.AddOptions<JobOptions>().BindConfiguration("Jobs");
        services.AddSingleton<ITicketJobQueue, ChannelTicketJobQueue>();
        services.AddSingleton<JobStore>();
        services.AddScoped<BulkCloseProcessor>();
        services.AddHostedService<TicketJobWorker>();

        services.AddOptions<SlaOptions>().BindConfiguration("Sla");
        services.AddSingleton<EscalationLog>();
        services.AddHostedService<SlaMonitor>();

        return services;
    }
}
```

Map the endpoint in `TicketingEndpoints.cs` (add `using Ticketing.Api.Features.Escalations;`):

```csharp
        api.MapGroup("/escalations")
            .WithTags("Escalations")
            .RequireAuthorization(Policies.Agent)
            .MapListEscalations();
```

And the settings, in `appsettings.json`:

```json
  "Sla": {
    "FirstResponseMinutes": 60,
    "ScanIntervalMs": 30000
  }
```

### Checkpoint

**All 14 passing.** Run the API, wait 30 seconds, and read `/api/v1/escalations`: two seeded tickets have been waiting two weeks.

---

## Step 6: Compare the Service Bus version (5 min)

One last change, for browsers: a cross-origin page can't read the `Location` header unless the API exposes it. Add it in `Infrastructure/CorsExtensions.cs`:

```csharp
            .WithExposedHeaders("X-Total-Count", "Link", "ETag", "Retry-After", "Location")));
```

Now open `Features/Jobs/ServiceBusTicketJobQueue.cs` next to your `ChannelTicketJobQueue.cs`. Same interface, so the endpoint, the worker, and the processor don't change at all. Moving to Azure is two registrations. What's different is what you **get**:

| | `Channel<T>` | Azure Service Bus |
|---|---|---|
| Survives a restart | No | Yes |
| Shared across instances | No: each instance has its own queue | Yes: any instance can take any job |
| Retries | You wrote them | Built in (redelivery on abandon) |
| Dead letters | You wrote them | Built in (after `MaxDeliveryCount`) |
| Duplicate detection | No | Optional, by `MessageId` |
| Cost to run | Nothing | A Service Bus namespace |

Read the comment in `DequeueAllAsync`. The simple interface forces it to complete messages as they're received, which makes it **at-most-once**: if the process dies mid-job, the job is lost. A production interface hands the worker a way to acknowledge (complete on success, abandon on failure), so the broker can redeliver. **The shape of an interface decides the guarantees its implementations can offer.**

---

## Finished

Your `Program.cs`:

```csharp
using Ticketing.Api.Chaos;
using Ticketing.Api.Features;
using Ticketing.Api.Features.Events;
using Ticketing.Api.Features.Jobs;
using Ticketing.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiDefaults();
builder.Services.AddErrorHandling();
builder.Services.AddTicketing();
builder.Services.AddTicketingResilience();
builder.Services.AddTicketEvents();
builder.Services.AddTicketJobs();
builder.Services.AddTicketingAuth();
builder.Services.AddTicketingRateLimits(builder.Configuration);
builder.Services.AddSupportSiteCors(builder.Configuration);
builder.Services.AddTicketingHealth(builder.Configuration);
builder.Services.AddTicketingTelemetry(builder.Configuration);

// Don't advertise the server software in every response
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

app.UseSecurityHeaders();
app.UseErrorHandling();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(CorsPolicies.SupportSite);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapOpenApi().WithDocumentPerVersion();
app.MapTicketingEndpoints();
app.MapTicketingHealth();
app.MapChaosEndpoints();

app.Run();

public partial class Program;
```

And `TicketingEndpoints.cs`, the whole API surface you built today:

```csharp
using Asp.Versioning;
using Ticketing.Api.Features.Comments;
using Ticketing.Api.Features.Escalations;
using Ticketing.Api.Features.Jobs;
using Ticketing.Api.Features.Tickets;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features;

public static class TicketingEndpoints
{
    /// <summary>
    /// The whole public surface of the API, in one place.
    /// </summary>
    public static IEndpointRouteBuilder MapTicketingEndpoints(this IEndpointRouteBuilder app)
    {
        // Every route lives under a version from day one, even with only v1
        var api = app.NewVersionedApi("Ticketing")
            .MapGroup("/api/v{version:apiVersion}")
            .HasApiVersion(new ApiVersion(1, 0))
            .RequireAuthorization();   // every ticket endpoint needs a signed-in user

        api.MapGroup("/tickets")
            .WithTags("Tickets")
            .MapListTickets()
            .MapGetTicket()
            .MapCreateTicket()
            .MapUpdateTicket()
            .MapAssignTicket()
            .MapCloseTicket()
            .MapBulkCloseTickets();

        api.MapGroup("/tickets/{ticketId:guid}/comments")
            .WithTags("Comments")
            .MapListComments()
            .MapAddComment();

        api.MapGroup("/jobs")
            .WithTags("Jobs")
            .RequireAuthorization(Policies.Agent)
            .MapGetJob();

        api.MapGroup("/escalations")
            .WithTags("Escalations")
            .RequireAuthorization(Policies.Agent)
            .MapListEscalations();

        return app;
    }
}
```

```
dotnet test
```

**All 109 tests, green.**

---

## Talk it over

### The gap between saving and announcing

`CloseTicket` saves the ticket, **then** publishes `TicketClosed`. If the process dies between the two, the ticket is closed and nobody hears about it. If the handler fails, the caller gets an error for a close that actually happened. In-process events can't fix that.

The **outbox pattern** does. In the same database transaction that saves the ticket, write the event to an `outbox` table. A background worker (like the one you just wrote) reads the outbox and publishes each event, marking it sent. Either both the change and the event are saved, or neither is. Delivery becomes at-least-once, so handlers must be safe to run twice, which is the same "resume, don't repeat" idea as Step 4.

### At-least-once means handlers must be idempotent

Every real queue delivers at least once, so every handler eventually sees a duplicate. Design for it: check whether the work is already done (Step 4), use a natural key (one closing comment per ticket close), or record processed message IDs.

### 202 isn't free

A 202 moves complexity to the caller: they poll, or you notify them (webhooks, which is Bonus Lab B, or server-sent events). Use it when the work is genuinely slow or can fail independently, not for every write. The `Retry-After: 1` header on an unfinished job tells well-behaved callers how often to ask.

### Background services in a fleet

Run three instances and you have three SLA monitors escalating the same ticket three times, and three in-memory job queues that can't see each other's jobs. In production: a shared queue (Service Bus) for jobs, and for scheduled work either a single dedicated worker, a distributed lock, or a scheduler built for it (Azure Functions timer triggers, Hangfire, Quartz.NET).

### Testing time

`SlaMonitor` reads `DateTimeOffset.UtcNow`, so the tests had to rely on two-week-old seed data. Injecting .NET's `TimeProvider` (and `FakeTimeProvider` in tests) lets a test move the clock forward an hour instead.
