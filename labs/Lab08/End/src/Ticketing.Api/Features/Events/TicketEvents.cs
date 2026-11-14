namespace Ticketing.Api.Features.Events;

/// <summary>Something that happened to a ticket, that other slices may care about.</summary>
public interface ITicketEvent;

public record TicketClosed(Guid TicketId, string ClosedBy) : ITicketEvent;

/// <summary>Reacts to one kind of event. A slice that cares implements this; the publisher never knows it exists.</summary>
public interface ITicketEventHandler<in TEvent> where TEvent : ITicketEvent
{
    Task HandleAsync(TEvent ticketEvent, CancellationToken cancellationToken);
}

public interface ITicketEvents
{
    Task PublishAsync<TEvent>(TEvent ticketEvent, CancellationToken cancellationToken) where TEvent : ITicketEvent;
}

/// <summary>
/// Runs every handler for an event, in-process, before PublishAsync returns.
/// Simple and fast, and not durable: if the process dies mid-publish, the event is gone.
/// </summary>
public class InProcessTicketEvents(IServiceProvider services) : ITicketEvents
{
    public async Task PublishAsync<TEvent>(TEvent ticketEvent, CancellationToken cancellationToken) where TEvent : ITicketEvent
    {
        foreach (var handler in services.GetServices<ITicketEventHandler<TEvent>>())
            await handler.HandleAsync(ticketEvent, cancellationToken);
    }
}

public static class TicketEventsExtensions
{
    public static IServiceCollection AddTicketEvents(this IServiceCollection services)
    {
        // Scoped: handlers may depend on per-request (or per-job) services
        services.AddScoped<ITicketEvents, InProcessTicketEvents>();
        services.AddScoped<ITicketEventHandler<TicketClosed>, Comments.AddClosingComment>();
        return services;
    }
}
