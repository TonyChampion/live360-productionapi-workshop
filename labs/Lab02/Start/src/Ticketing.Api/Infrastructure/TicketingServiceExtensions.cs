using Ticketing.Api.Chaos;
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

        // Decorator, registered by hand: callers ask for ITicketStore and get the
        // fault-injecting wrapper around the real store.
        services.AddSingleton<ITicketStore>(sp =>
            new FaultInjectingTicketStore(
                sp.GetRequiredService<InMemoryTicketStore>(),
                sp.GetRequiredService<ChaosSettings>()));

        return services;
    }
}
