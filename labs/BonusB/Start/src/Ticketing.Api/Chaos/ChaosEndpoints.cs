namespace Ticketing.Api.Chaos;

/// <summary>
/// Lab infrastructure, not lab content: lets the client (or a .http file) make the store misbehave.
/// </summary>
public static class ChaosEndpoints
{
    public static IEndpointRouteBuilder MapChaosEndpoints(this IEndpointRouteBuilder app)
    {
        var chaos = app.MapGroup("/dev/chaos").WithTags("Chaos").ExcludeFromDescription();

        chaos.MapGet("/", (ChaosSettings settings) => settings.Current);
        chaos.MapPut("/", (ChaosState state, ChaosSettings settings) => settings.Apply(state));
        chaos.MapPost("/reset", (ChaosSettings settings) => settings.Reset());

        // Recent request traces for the lab client's trace panel
        _ = RecentTraces.Instance;
        app.MapGet("/dev/traces", () => RecentTraces.Instance.Recent()).ExcludeFromDescription();

        // An unhandled exception on demand, for checking what callers see when code fails
        app.MapGet("/dev/boom", () =>
        {
            throw new InvalidOperationException("Boom: an unhandled exception from deep inside the API.");
        }).ExcludeFromDescription();

        return app;
    }
}
