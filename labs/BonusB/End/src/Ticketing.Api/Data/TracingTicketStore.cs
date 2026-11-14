using System.Diagnostics;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Data;

/// <summary>
/// Fourth decorator: a span around every real store call. It sits inside the cache, so a cache
/// hit produces no span at all, and the trace shows exactly when the store was touched.
/// </summary>
public class TracingTicketStore(ITicketStore inner) : ITicketStore
{
    public Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Traced("ticket-store get", id, () => inner.GetAsync(id, cancellationToken),
            (span, ticket) => span.SetTag("ticket.found", ticket is not null));

    public Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default) =>
        Traced("ticket-store list", null, () => inner.ListAsync(cancellationToken),
            (span, tickets) => span.SetTag("ticket.count", tickets.Count));

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        Traced("ticket-store add", ticket.Id, async () => { await inner.AddAsync(ticket, cancellationToken); return true; });

    public Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        Traced("ticket-store update", ticket.Id, async () => { await inner.UpdateAsync(ticket, cancellationToken); return true; });

    public Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default) =>
        Traced("ticket-store add-comment", ticketId, () => inner.AddCommentAsync(ticketId, comment, cancellationToken));

    private static async Task<T> Traced<T>(string name, Guid? ticketId, Func<Task<T>> call, Action<Activity, T>? onResult = null)
    {
        // StartActivity returns null when nothing is listening, so tracing costs nothing when it's off
        using var span = Telemetry.Source.StartActivity(name);
        span?.SetTag("db.system.name", "in-memory");
        if (ticketId is not null) span?.SetTag("ticket.id", ticketId.ToString());

        try
        {
            var result = await call();
            if (span is not null) onResult?.Invoke(span, result);
            return result;
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            span?.AddException(ex);
            throw;
        }
    }
}
