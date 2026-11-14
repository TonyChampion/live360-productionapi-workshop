using System.Diagnostics;
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

    public async Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var hit = true;
        var caller = Activity.Current;
        var ticket = await cache.GetOrCreateAsync(
            Key(id),
            async token =>
            {
                hit = false;
                // HybridCache runs this factory outside the caller's context (one call can serve
                // several concurrent callers), so the caller's span is lost. Re-attach it, or the
                // store span lands in a separate trace and this request's trace has a gap.
                Activity.Current = caller;
                return await inner.GetAsync(id, token);
            },
            Entry,
            cancellationToken: cancellationToken);

        // Mark the current span, so a trace shows whether this read was served from cache
        Activity.Current?.AddEvent(new ActivityEvent(hit ? "cache.hit" : "cache.miss",
            tags: new ActivityTagsCollection { ["ticket.id"] = id.ToString() }));

        return ticket;
    }

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
