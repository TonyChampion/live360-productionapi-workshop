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
