using System.Collections.Concurrent;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

/// <summary>
/// Thread-safe in-memory store. Must be registered as a singleton:
/// any other lifetime gives each request (or scope) its own empty store.
/// </summary>
public class InMemoryTicketStore : ITicketStore
{
    private readonly ConcurrentDictionary<Guid, Ticket> _tickets = new();

    public InMemoryTicketStore()
    {
        foreach (var ticket in SeedData.Tickets())
            _tickets[ticket.Id] = ticket;
    }

    public Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_tickets.TryGetValue(id, out var ticket) ? ticket : null);

    public Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Ticket>>(_tickets.Values.OrderBy(t => t.CreatedAt).ToList());

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        _tickets[ticket.Id] = ticket;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        _tickets[ticket.Id] = ticket;
        return Task.CompletedTask;
    }

    public Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default)
    {
        if (!_tickets.TryGetValue(ticketId, out var ticket))
            return Task.FromResult<Comment?>(null);

        comment.TicketId = ticketId;
        lock (ticket.Comments)
            ticket.Comments.Add(comment);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult<Comment?>(comment);
    }
}
