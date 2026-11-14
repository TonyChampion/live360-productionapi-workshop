# Lab 7: Fail on Purpose

**Module 7: Resilience** | about 45 to 60 minutes

Every dependency fails eventually. The demo version handles that by waiting: a store that hangs for 10 seconds holds the request (and a server thread) for 10 seconds, a single blip becomes a 500, and an outage means every caller waits out every failure. In this lab you'll design how the API fails: within a known time budget, retrying only what's safe, and backing off a store that's down instead of piling on.

**You're done when** a transient blip is invisible to callers, a hung store is a 504 inside the budget, a down store is a fast 503 with `Retry-After`, an impossible budget refuses to start, and `dotnet test` is fully green.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/Lab07/Start` and run this lab's tests:

```
dotnet test --filter "Lab=7"
```

**9 failures, 1 passing.** The passing one is `A_disconnected_client_cancels_the_write`. Back in Lab 1 you passed `CancellationToken` into every store call. This test proves it matters: when a caller hangs up, the write stops, and no ghost ticket appears a second later. Keep it green; it's easy to break in this lab.

### What's new in your Start folder

- **The chaos panel has a deterministic failure knob.** `failNextCalls: 2` makes exactly the next two store calls fail, then everything's fine. Random error rates make flaky tests; this doesn't.
- **Three run cases:** a transient blip, a hung store, and tripping the circuit breaker.
- **`http/07-resilience.http`** walks through each failure.
- **The `Microsoft.Extensions.Resilience` package version** is pinned. It's Microsoft's packaging of [Polly](https://www.pollydocs.org/), the standard .NET resilience library.

### How the tests stay independent

Each Lab 7 test runs in **its own copy of the app** (`factory.WithWebHostBuilder`), with its own store, chaos settings, and circuit breaker, and a small, fast timeout budget. A circuit breaker opened by one test would otherwise leak into the next.

### See the problem first (3 min)

Run the API, sign in from the lab client, and try the three Lab 7 run cases. Today a blip is a 500, the hung store makes you wait the full 10 seconds, and the "breaker" never trips: every call waits out every failure.

---

## Step 1: Set the budget (10 min)

Before any retry or timeout code, decide **how long a request is allowed to take**, from the outside in:

| Layer | Budget | What happens when it runs out |
|---|---|---|
| The whole request | 5 s | ASP.NET Core gives up and returns 504 |
| All store attempts together | 2.5 s | The store call is cancelled; 504 with a Problem |
| One store attempt | 1 s | That attempt is cancelled; it may be retried |

The rule that matters: **each layer has to fit inside the one around it.** If the store's budget were longer than the request's, the request would give up first and the caller would get a generic timeout instead of your designed error. That's a configuration mistake, so catch it where configuration mistakes belong: at startup.

Add the package to `src/Ticketing.Api/Ticketing.Api.csproj`:

```xml
    <PackageReference Include="Microsoft.Extensions.Resilience" />
```

Create `src/Ticketing.Api/Infrastructure/ResilienceExtensions.cs` with the budget and its validation:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;

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

        return services;
    }
}
```

Put the budget in `appsettings.json`:

```json
  "Resilience": {
    "RequestTimeoutMs": 5000,
    "StoreTotalTimeoutMs": 2500,
    "StoreAttemptTimeoutMs": 1000,
    "StoreMaxRetries": 2,
    "StoreRetryDelayMs": 100,
    "BreakerFailureRatio": 0.5,
    "BreakerMinimumThroughput": 10,
    "BreakerSamplingSeconds": 30,
    "BreakDurationSeconds": 15
  }
```

In `Program.cs`, register it after `AddTicketing()`:

```csharp
builder.Services.AddTicketingResilience();
```

And add the middleware after `UseRateLimiter()`:

```csharp
app.UseRequestTimeouts();
```

### Checkpoint

**3 passing.** Try it: set `StoreTotalTimeoutMs` to `6000` and run the API. It refuses to start, and tells you why. Put it back.

> Request timeouts don't apply while a debugger is attached, so they won't fire if you're stepping through code.

---

## Step 2: Dependency failures are not bugs (5 min)

A store that's down is not your code's fault, and the caller should hear the difference: **503** means "a dependency is failing, try again," **504** means "it took too long." Neither belongs in the Error log next to real bugs.

Add the types to `ProblemTypes` in `Infrastructure/Problems.cs`:

```csharp
    public const string StoreUnavailable = Base + "store-unavailable";  // 503: a dependency is failing
    public const string Timeout = Base + "timeout";                     // 504: ran out of time
```

Create `src/Ticketing.Api/Infrastructure/DependencyExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Ticketing.Api.Chaos;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// Dependency failures are expected in production, not bugs: they get their own status codes,
/// a Retry-After where it helps, and a Warning in the log instead of an Error.
/// Registered ahead of GlobalExceptionHandler; anything it doesn't recognize falls through.
/// </summary>
public class DependencyExceptionHandler(
    IProblemDetailsService problemDetails,
    IOptions<ResilienceOptions> budget,
    ILogger<DependencyExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, type, title, detail, retryAfter) = exception switch
        {
            BrokenCircuitException => (503, ProblemTypes.StoreUnavailable, "The ticket store is unavailable",
                "The store has been failing, so calls are paused to let it recover. Try again after the Retry-After interval.",
                (TimeSpan?)TimeSpan.FromSeconds(budget.Value.BreakDurationSeconds)),
            StoreUnavailableException or TransientStoreException => (503, ProblemTypes.StoreUnavailable,
                "The ticket store is unavailable", "The store couldn't complete the request. Try again shortly.", null),
            TimeoutRejectedException => (504, ProblemTypes.Timeout, "The ticket store didn't respond in time",
                "The request ran out of time waiting for the store. Try again shortly.", null),
            _ => (0, "", "", "", null)
        };

        if (status == 0)
            return false;   // not a dependency failure: let GlobalExceptionHandler deal with it

        logger.LogWarning("{Method} {Path} failed: {Title} ({ExceptionType})",
            context.Request.Method, context.Request.Path, title, exception.GetType().Name);

        context.Response.StatusCode = status;
        if (retryAfter is not null)
            context.Response.Headers.RetryAfter = ((int)retryAfter.Value.TotalSeconds).ToString();

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Type = type, Title = title, Detail = detail }
        });
    }
}
```

Register it in `AddErrorHandling()`. **Order matters**: exception handlers are tried in the order they're registered, so the specific one goes before Lab 2's safety net:

```csharp
        // Handlers are tried in the order they're registered: specific first, the safety net last
        services.AddExceptionHandler<DependencyExceptionHandler>();
        services.AddExceptionHandler<GlobalExceptionHandler>();
```

And in the same file, give a 504 from the request timeout middleware its catalog type, in the `problem.Status switch`:

```csharp
                        StatusCodes.Status504GatewayTimeout => ProblemTypes.Timeout,
```

### Checkpoint

**5 passing.** A down store is now a 503, not a 500. Nothing is retried yet.

---

## Step 3: Timeouts and retries (10 min)

Now the pipeline itself. Strategies wrap each other like layers, outermost first: the **total** timeout around the **retry** around each **attempt's** timeout. Replace `ResilienceExtensions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using Polly;
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

                // 3. Each individual attempt
                .AddTimeout(TimeSpan.FromMilliseconds(budget.StoreAttemptTimeoutMs));
        });

        return services;
    }
}
```

**What gets retried is the important part:**

| Failure | Retried? | Why |
|---|---|---|
| Transient error | Yes | The store refused before doing anything, so trying again is safe |
| Timeout on a **read** | Yes | Reading twice does no harm |
| Timeout on a **write** | **No** | The write may have happened. Retrying could create the ticket twice |
| Store down | No | Retrying a dead dependency only adds load. That's Step 4's job |

Exponential backoff with **jitter** spreads retries out, so a thousand callers that all failed at once don't all retry at the same millisecond.

Now the fifth decorator. Create `src/Ticketing.Api/Data/ResilientTicketStore.cs`:

```csharp
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
```

Add it to the chain in `Infrastructure/TicketingServiceExtensions.cs` (add `using Polly.Registry;`):

```csharp
using Ticketing.Api.Chaos;
using Microsoft.Extensions.Caching.Hybrid;
using Polly.Registry;
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

        // Decorators, registered by hand. Order matters:
        //   caller -> Caching -> Resilient -> Tracing -> FaultInjecting -> InMemory
        // A cache hit skips everything. Each retry attempt is traced as its own span.
        services.AddSingleton<ITicketStore>(sp =>
            new CachingTicketStore(
                new ResilientTicketStore(
                    new TracingTicketStore(
                        new FaultInjectingTicketStore(
                            sp.GetRequiredService<InMemoryTicketStore>(),
                            sp.GetRequiredService<ChaosSettings>())),
                    sp.GetRequiredService<ResiliencePipelineProvider<string>>()),
                sp.GetRequiredService<HybridCache>()));

        services.AddSingleton<Features.Tickets.TicketLog>();

        return services;
    }
}
```

It sits **outside** tracing, so each retry attempt is its own span, and **inside** the cache, so a cache hit never pays for any of this.

### Checkpoint

**7 passing.** Run **Transient blip** in the lab client and open its trace: three `ticket-store list` spans, the first two red, and a 200 for the caller. Run **Hung store**: a 504 in about 2.5 seconds instead of 10.

### Try it: drop the token (optional, 3 min)

In `ResilientTicketStore.Run`, change `call(ctx.CancellationToken)` to `call(CancellationToken.None)` and run the tests. **Three** go red, not one: the ghost write is back, and so are the hung requests. Polly's timeouts are **cooperative**: they cancel a token, and if the code underneath ignores it, nothing stops. Put it back.

---

## Step 4: Stop calling a store that's down (5 min)

Retries help with blips. During a real outage they make things worse: every request still waits out every failure, and the struggling store gets three times the traffic. A **circuit breaker** watches the failure rate. Past a threshold it **opens**: calls fail immediately, without touching the store. After a break it lets one trial call through (**half-open**); if that succeeds, it **closes** again.

Add it to the pipeline, between the retry and the attempt timeout. Here's the finished `ResilienceExtensions.cs`:

```csharp
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
```

The `CircuitBreakerStateProvider` lets other code read the breaker's state. You'll use it in a minute.

### Checkpoint

**9 passing.** Run **Trip the circuit breaker**: slow 503s at first, then instant 503s with `Retry-After`. The trace for a fail-fast request has no store span at all: the store wasn't called.

---

## Step 5: Health while the circuit is open (5 min)

When the breaker is open, what should `/health` say? It's a real judgment call:

- **Unhealthy (503)** takes the instance out of the load balancer. But every instance shares the same store, so every instance goes unhealthy at once, and a partial outage becomes a total one. Callers get the load balancer's error instead of yours.
- **Degraded (200)** keeps the instance in rotation. It still serves cached tickets (Lab 5), and everything else fails fast with a clear 503 and `Retry-After`.

This API chooses **Degraded**. It also stops probing the store while the circuit is open: the breaker already knows, and its half-open trial is the probe. Update `StoreHealthCheck` in `Infrastructure/HealthExtensions.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Ticketing.Api.Data;

namespace Ticketing.Api.Infrastructure;

/// <summary>Bound from HealthChecks:Store.</summary>
public class StoreHealthOptions
{
    public int DegradedMs { get; set; } = 500;
    public int TimeoutMs { get; set; } = 2000;
}

/// <summary>
/// Is the ticket store doing its job? Not just "can I reach it": slow counts as degraded, and
/// no answer within the timeout counts as unhealthy.
/// </summary>
public class StoreHealthCheck(
    ITicketStore store, IOptions<StoreHealthOptions> options, CircuitBreakerStateProvider circuit) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The breaker already knows the store is failing. Don't add to the load by probing it;
        // report Degraded: this instance still serves cached reads and fails the rest fast.
        if (circuit.CircuitState is CircuitState.Open or CircuitState.Isolated)
            return HealthCheckResult.Degraded("Circuit open: the store has been failing, so calls are paused while it recovers.");

        var limits = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.TimeoutMs);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await store.ListAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"No response within {limits.TimeoutMs} ms.");
        }
        catch (BrokenCircuitException)
        {
            return HealthCheckResult.Degraded("Circuit open: the store has been failing, so calls are paused while it recovers.");
        }
        catch (Exception)
        {
            // Deliberately not passing the exception: health output is public
            return HealthCheckResult.Unhealthy("The ticket store is unavailable.");
        }

        var elapsed = stopwatch.ElapsedMilliseconds;
        var data = new Dictionary<string, object> { ["responseTimeMs"] = elapsed };
        return elapsed > limits.DegradedMs
            ? HealthCheckResult.Degraded($"Responding slowly ({elapsed} ms).", data: data)
            : HealthCheckResult.Healthy("Responding normally.", data);
    }
}

public static class HealthExtensions
{
    private const string Ready = "ready";

    public static IServiceCollection AddTicketingHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StoreHealthOptions>(configuration.GetSection("HealthChecks:Store"));
        services.AddHealthChecks()
            .AddCheck<StoreHealthCheck>("ticket-store", tags: [Ready]);
        return services;
    }

    public static WebApplication MapTicketingHealth(this WebApplication app)
    {
        // Everything, for people and dashboards
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteJson });

        // Liveness: is the process alive? No dependencies, or a store outage restarts every instance
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteJson });

        // Readiness: should this instance get traffic right now?
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(Ready),
            ResponseWriter = WriteJson
        });

        return app;
    }

    /// <summary>
    /// A JSON report. The built-in writer only sends the word "Healthy". This writer sends each
    /// check's status, description, timing, and data, and never an exception.
    /// </summary>
    private static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                data = entry.Value.Data
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
```

### Checkpoint

**All 10 passing.** Trip the breaker again and watch the health strip go amber, not red.

---

## Finished

Your `Program.cs`:

```csharp
using Ticketing.Api.Chaos;
using Ticketing.Api.Features;
using Ticketing.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiDefaults();
builder.Services.AddErrorHandling();
builder.Services.AddTicketing();
builder.Services.AddTicketingResilience();
builder.Services.AddTicketingAuth();
builder.Services.AddTicketingRateLimits(builder.Configuration);
builder.Services.AddSupportSiteCors(builder.Configuration);
builder.Services.AddTicketingHealth(builder.Configuration);
builder.Services.AddTicketingTelemetry(builder.Configuration);

// Don't advertise the server software in every response
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

app.UseSecurityHeaders();
app.UseErrorHandling();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(CorsPolicies.SupportSite);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapOpenApi().WithDocumentPerVersion();
app.MapTicketingEndpoints();
app.MapTicketingHealth();
app.MapChaosEndpoints();

app.Run();

public partial class Program;
```

```
dotnet test
```

**All green.** Work through `http/07-resilience.http` and the three run cases, with the trace panel open.

---

## Talk it over

### Retries and idempotency

You didn't retry timed-out writes because the write might have happened. The better fix is to make writes **safe to repeat**: the caller sends an `Idempotency-Key`, and the API returns the original result if it sees the same key twice. That's **Bonus Lab A**. With idempotent writes, retrying them becomes safe again, on your side and the caller's.

### Retries multiply

If the client retries 3 times, your API retries the store 3 times, and the store retries its database 3 times, one user's click becomes 27 database calls during an incident. Retry at **one** layer, usually the one closest to the failure, and make sure every layer's budget fits inside its caller's.

### HTTP dependencies

Calling another API with `HttpClient`? You don't need to build this by hand. `Microsoft.Extensions.Http.Resilience` gives you the same pipeline with one line:

```csharp
builder.Services.AddHttpClient<BillingClient>().AddStandardResilienceHandler();
```

It's a total timeout, retry, circuit breaker, and attempt timeout, in the order you just built. Now you know what it does, and how to tune it.

### Tuning the breaker

A breaker that opens too easily turns small blips into outages; one that never opens is decoration. Start from real traffic: the minimum throughput should be well above what a quiet minute produces, so a handful of errors at 3am doesn't open it. And alert on breaker state changes: an open circuit is the clearest signal you'll get that a dependency is in trouble.
