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

        // Decorators, registered by hand. Order matters: the cache sits OUTSIDE the
        // fault injector, so a cached ticket can still be served while the store is down.
        //   caller -> CachingTicketStore -> FaultInjectingTicketStore -> InMemoryTicketStore
        services.AddSingleton<ITicketStore>(sp =>
            new CachingTicketStore(
                new FaultInjectingTicketStore(
                    sp.GetRequiredService<InMemoryTicketStore>(),
                    sp.GetRequiredService<ChaosSettings>()),
                sp.GetRequiredService<HybridCache>()));

        return services;
    }
}
