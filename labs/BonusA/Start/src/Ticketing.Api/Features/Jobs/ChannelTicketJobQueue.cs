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
