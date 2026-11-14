namespace Ticketing.Api.Infrastructure;

public static class IdempotencyExtensions
{
    public static IServiceCollection AddIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdempotencyOptions>(configuration.GetSection("Idempotency"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IdempotencyStore>();   // singleton: it must outlive any one request
        return services;
    }

    /// <summary>Makes a POST safe to retry with an Idempotency-Key header.</summary>
    public static RouteHandlerBuilder WithIdempotency<TRequest>(this RouteHandlerBuilder builder) =>
        builder
            .AddEndpointFilter<IdempotencyFilter<TRequest>>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
