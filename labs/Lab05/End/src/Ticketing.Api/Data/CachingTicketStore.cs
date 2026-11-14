using Microsoft.Extensions.Caching.Hybrid;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

/// <summary>
/// Third decorator on the same interface. Single-ticket reads come from the cache;
/// every write evicts that ticket, so the next read goes back to the store.
///
/// It caches data, not HTTP responses, so authorization still runs on every request.
/// </summary>
public class CachingTicketStore(ITicketStore inner, HybridCache cache) : ITicketStore
{
    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private static string Key(Guid id) => $"ticket:{id}";

    public async Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await cache.GetOrCreateAsync(
            Key(id),
            async token => await inner.GetAsync(id, token),
            Entry,
            cancellationToken: cancellationToken);

    // Lists vary by caller, filter, and page, and change whenever any ticket does. Not cached.
    public Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default) =>
        inner.ListAsync(cancellationToken);

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        inner.AddAsync(ticket, cancellationToken);

    public async Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        await inner.UpdateAsync(ticket, cancellationToken);
        await cache.RemoveAsync(Key(ticket.Id), cancellationToken);
    }

    public async Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default)
    {
        var added = await inner.AddCommentAsync(ticketId, comment, cancellationToken);
        await cache.RemoveAsync(Key(ticketId), cancellationToken);
        return added;
    }
}
