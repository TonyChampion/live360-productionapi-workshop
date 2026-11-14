using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Ticketing.Api.Chaos;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// The timeout budget, from the outside in, bound from the "Resilience" section:
///   request (whole HTTP request)  >  store total (all attempts and delays)  >=  store attempt (one call)
/// Plus how many retries fit in the budget, and when to stop calling the store altogether.
/// </summary>
public class ResilienceOptions
{
    [Range(100, 120_000)] public int RequestTimeoutMs { get; set; } = 5000;
    [Range(50, 60_000)] public int StoreTotalTimeoutMs { get; set; } = 2500;
    [Range(10, 60_000)] public int StoreAttemptTimeoutMs { get; set; } = 1000;
    [Range(1, 10)] public int StoreMaxRetries { get; set; } = 2;
    [Range(0, 10_000)] public int StoreRetryDelayMs { get; set; } = 100;

    [Range(0.01, 1.0)] public double BreakerFailureRatio { get; set; } = 0.5;
    [Range(2, 1000)] public int BreakerMinimumThroughput { get; set; } = 10;
    [Range(1, 600)] public int BreakerSamplingSeconds { get; set; } = 30;
    [Range(1, 600)] public int BreakDurationSeconds { get; set; } = 15;
}

public static class StoreResilience
{
    public const string Pipeline = "ticket-store";

    /// <summary>Set per call by the decorator: is this call safe to repeat?</summary>
    public static readonly ResiliencePropertyKey<bool> IsRead = new("ticket-store.is-read");
}

public static class ResilienceExtensions
{
    public static IServiceCollection AddTicketingResilience(this IServiceCollection services)
    {
        // Check the budget when the app starts, not when the first request times out
        services.AddOptions<ResilienceOptions>()
            .BindConfiguration("Resilience")
            .ValidateDataAnnotations()
            .Validate(o => o.StoreAttemptTimeoutMs <= o.StoreTotalTimeoutMs,
                "Resilience: one store attempt can't have more time than all attempts together.")
            .Validate(o => o.StoreTotalTimeoutMs < o.RequestTimeoutMs,
                "Resilience: the store's budget must fit inside the request's, or the request gives up first and the caller never sees a designed error.")
            .ValidateOnStart();

        // Outermost layer: no request runs longer than this, whatever it's waiting on
        services.AddRequestTimeouts();
        services.AddOptions<RequestTimeoutOptions>()
            .Configure<IOptions<ResilienceOptions>>((options, budget) =>
                options.DefaultPolicy = new RequestTimeoutPolicy
                {
                    Timeout = TimeSpan.FromMilliseconds(budget.Value.RequestTimeoutMs),
                    TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
                });

        // Lets the health check see whether the breaker is open
        services.AddSingleton<CircuitBreakerStateProvider>();

        services.AddResiliencePipeline(StoreResilience.Pipeline, (pipeline, context) =>
        {
            var budget = context.ServiceProvider.GetRequiredService<IOptions<ResilienceOptions>>().Value;

            pipeline
                // 1. The whole call, every attempt and delay included
                .AddTimeout(TimeSpan.FromMilliseconds(budget.StoreTotalTimeoutMs))

                // 2. Retry, but only what's safe to repeat
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = budget.StoreMaxRetries,
                    Delay = TimeSpan.FromMilliseconds(budget.StoreRetryDelayMs),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,   // so a thousand callers don't retry in lockstep
                    ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception switch
                    {
                        // The store refused before doing anything: always safe to try again
                        TransientStoreException => true,
                        // No answer in time: a read can be repeated, but a write may have happened
                        TimeoutRejectedException => args.Context.Properties.GetValue(StoreResilience.IsRead, false),
                        // Down is down; that's the breaker's job, and retrying only adds load
                        _ => false
                    })
                })

                // 3. Stop calling a store that keeps failing; let it recover
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = budget.BreakerFailureRatio,
                    MinimumThroughput = budget.BreakerMinimumThroughput,
                    SamplingDuration = TimeSpan.FromSeconds(budget.BreakerSamplingSeconds),
                    BreakDuration = TimeSpan.FromSeconds(budget.BreakDurationSeconds),
                    ShouldHandle = new PredicateBuilder()
                        .Handle<TransientStoreException>()
                        .Handle<StoreUnavailableException>()
                        .Handle<TimeoutRejectedException>(),
                    StateProvider = context.ServiceProvider.GetRequiredService<CircuitBreakerStateProvider>()
                })

                // 4. Each individual attempt
                .AddTimeout(TimeSpan.FromMilliseconds(budget.StoreAttemptTimeoutMs));
        });

        return services;
    }
}
