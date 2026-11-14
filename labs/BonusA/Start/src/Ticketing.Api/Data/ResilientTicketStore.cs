using Polly;
using Polly.Registry;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Data;

/// <summary>
/// Fifth decorator: every store call runs through the resilience pipeline (timeouts, retries,
/// circuit breaker). Outside the tracing decorator, so each retry attempt is its own span.
/// </summary>
public class ResilientTicketStore(ITicketStore inner, ResiliencePipelineProvider<string> pipelines) : ITicketStore
{
    private readonly ResiliencePipeline _pipeline = pipelines.GetPipeline(StoreResilience.Pipeline);

    public Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Run(token => inner.GetAsync(id, token), isRead: true, cancellationToken);

    public Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default) =>
        Run(token => inner.ListAsync(token), isRead: true, cancellationToken);

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        Run(async token => { await inner.AddAsync(ticket, token); return true; }, isRead: false, cancellationToken);

    public Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        Run(async token => { await inner.UpdateAsync(ticket, token); return true; }, isRead: false, cancellationToken);

    public Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default) =>
        Run(token => inner.AddCommentAsync(ticketId, comment, token), isRead: false, cancellationToken);

    private async Task<T> Run<T>(Func<CancellationToken, Task<T>> call, bool isRead, CancellationToken cancellationToken)
    {
        // The context carries the caller's token in, and tells the retry strategy what kind of call this is
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(StoreResilience.IsRead, isRead);
        try
        {
            // Pass the PIPELINE's token to the store, not the caller's: it's the caller's token
            // linked with the timeouts, so a timeout actually stops the call
            return await _pipeline.ExecuteAsync(
                static async (ctx, call) => await call(ctx.CancellationToken), context, call);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
