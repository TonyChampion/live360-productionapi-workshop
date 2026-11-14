namespace Ticketing.Api.Features.Jobs;

/// <summary>
/// A unit of background work: close these tickets on behalf of this user.
/// Carries the trace context of the request that queued it, so the work shows up in the same trace.
/// </summary>
/// <param name="Attempt">1 for the first try; incremented each time the job is retried.</param>
public record TicketJob(Guid Id, Guid[] TicketIds, string RequestedBy, string? TraceParent, int Attempt = 1);

/// <summary>
/// The boundary between "accept the work" and "do the work". The API depends on this interface,
/// never on a transport: an in-memory channel on your laptop, Azure Service Bus in production.
/// </summary>
public interface ITicketJobQueue
{
    ValueTask EnqueueAsync(TicketJob job, CancellationToken cancellationToken = default);

    /// <summary>Jobs as they arrive, until the app shuts down.</summary>
    IAsyncEnumerable<TicketJob> DequeueAllAsync(CancellationToken cancellationToken);
}
