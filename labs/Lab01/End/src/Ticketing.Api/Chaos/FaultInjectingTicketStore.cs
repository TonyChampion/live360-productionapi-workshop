using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Chaos;

/// <summary>
/// Decorator around the real store. Same interface, so nothing upstream knows it's here.
/// Adds latency, random transient errors, or a full outage based on <see cref="ChaosSettings"/>.
/// </summary>
public class FaultInjectingTicketStore(ITicketStore inner, ChaosSettings settings) : ITicketStore
{
    public async Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await InjectAsync(cancellationToken);
        return await inner.GetAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default)
    {
        await InjectAsync(cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        await InjectAsync(cancellationToken);
        await inner.AddAsync(ticket, cancellationToken);
    }

    public async Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        await InjectAsync(cancellationToken);
        await inner.UpdateAsync(ticket, cancellationToken);
    }

    public async Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default)
    {
        await InjectAsync(cancellationToken);
        return await inner.AddCommentAsync(ticketId, comment, cancellationToken);
    }

    private async Task InjectAsync(CancellationToken cancellationToken)
    {
        var state = settings.Current;

        if (!state.Available)
            throw new StoreUnavailableException("Ticket store is unavailable.");

        if (state.LatencyMs > 0)
            await Task.Delay(state.LatencyMs, cancellationToken);

        if (settings.TryConsumeFailure() || (state.ErrorRate > 0 && Random.Shared.NextDouble() < state.ErrorRate))
            throw new TransientStoreException("Ticket store returned a transient error.");
    }
}
