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
