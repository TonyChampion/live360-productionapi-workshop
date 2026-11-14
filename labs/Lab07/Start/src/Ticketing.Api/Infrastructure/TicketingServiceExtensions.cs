using Ticketing.Api.Chaos;
using Microsoft.Extensions.Caching.Hybrid;
using Ticketing.Api.Data;

namespace Ticketing.Api.Infrastructure;

public static class TicketingServiceExtensions
{
    /// <summary>
    /// Data access for the ticketing feature: the in-memory store, wrapped by fault injection.
    /// </summary>
    public static IServiceCollection AddTicketing(this IServiceCollection services)
    {
        // Singleton: the store IS the data. Scoped would hand every request an empty store.
        services.AddSingleton<ChaosSettings>();
        services.AddSingleton<InMemoryTicketStore>();

        services.AddHybridCache();

        // Decorators, registered by hand. Order matters: the cache sits OUTSIDE everything,
        // so a cached ticket is served without touching (or tracing) the store at all.
        //   caller -> Caching -> Tracing -> FaultInjecting -> InMemory
        services.AddSingleton<ITicketStore>(sp =>
            new CachingTicketStore(
                new TracingTicketStore(
                    new FaultInjectingTicketStore(
                        sp.GetRequiredService<InMemoryTicketStore>(),
                        sp.GetRequiredService<ChaosSettings>())),
                sp.GetRequiredService<HybridCache>()));

        services.AddSingleton<Features.Tickets.TicketLog>();

        return services;
    }
}
