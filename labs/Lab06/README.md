# Lab 6: Make It Report on Itself

**Module 6: Observability** | about 45 to 60 minutes

When something goes wrong in production, you have three questions: **is it working** (health), **where did the time go** (traces), and **what happened** (logs). The demo version answers none of them: `/health` says "Healthy" while the store is down, nothing records what a request did, and the logs are sentences you can only grep. In this lab the API learns to report on itself.

**You're done when** health reflects the store and speaks JSON, every store call appears as a span inside its request's trace, ticket changes are logged as structured events, and `dotnet test` is fully green.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/Lab06/Start` and run this lab's tests:

```
dotnet test --filter "Lab=6"
```

**9 failures, 1 passing.** The passing one is `An_error_can_be_found_in_the_logs_from_the_callers_trace_id`. Hold that thought: you'll find out why it already works in Step 4.

### What's new in your Start folder

- **The lab client has a "Recent traces" panel.** It draws each recent API request as a waterfall of spans. Right now every request is a single bar, because your code doesn't create any spans yet.
- **A "Store outage: watch health" run case** takes the store down for 10 seconds.
- **`http/06-observability.http`** covers health, chaos, and traces.
- **OpenTelemetry package versions** are pinned, and the test project references the SDK so a test can check you registered it.

The trace panel is lab infrastructure (`Chaos/RecentTraces.cs`), like the chaos panel. In production you'd send the same spans to a tracing backend instead. Step 2 sets that up.

---

## Step 1: Health that means something (10 min)

Take the store down (**Down** in the chaos panel) and open `/health`. It says `Healthy`. The default health endpoint only proves the process answered, which a load balancer already knew.

A real health check asks a dependency to do its job. Create `src/Ticketing.Api/Infrastructure/HealthExtensions.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
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
public class StoreHealthCheck(ITicketStore store, IOptions<StoreHealthOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
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

Three ideas in there:

- **Degraded is a real state.** A store answering in 700 ms isn't down, but it isn't fine either. Degraded still returns **200**, so traffic keeps flowing, while dashboards and alerts can see the trouble. Unhealthy returns **503**.
- **Liveness and readiness are different questions.** An orchestrator like Kubernetes **restarts** an instance that fails liveness, and **stops sending traffic** to one that fails readiness. If liveness checked the store, a store outage would restart every instance at once, which fixes nothing and makes recovery slower. So `/health/live` checks nothing but the process.
- **The JSON writer leaves out exceptions.** Health endpoints are usually public. The built-in writer sends only the word "Healthy"; this one sends what a person needs, and nothing an attacker can use.

Add the thresholds to `appsettings.json`:

```json
  "HealthChecks": {
    "Store": {
      "DegradedMs": 500,
      "TimeoutMs": 2000
    }
  }
```

In `Program.cs`, replace `builder.Services.AddHealthChecks();` with:

```csharp
builder.Services.AddTicketingHealth(builder.Configuration);
```

And replace `app.MapHealthChecks("/health");` with:

```csharp
app.MapTicketingHealth();
```

### Checkpoint

**6 passing.** Run **Store outage: watch health** in the lab client. The health strip goes red within a few seconds, then recovers. Try **Slow (800 ms)** too: the strip goes amber.

---

## Step 2: Register OpenTelemetry (5 min)

Tracing in .NET has two halves, and they're separate on purpose:

- **Creating spans** uses `System.Diagnostics.Activity`, built into .NET. Libraries (including ASP.NET Core itself) create spans without depending on any tracing product.
- **Exporting spans** is OpenTelemetry's job: collect them, add context like the service name, and send them to a backend.

Add the packages to `src/Ticketing.Api/Ticketing.Api.csproj`:

```xml
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
```

Create `src/Ticketing.Api/Infrastructure/Telemetry.cs`:

```csharp
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ticketing.Api.Infrastructure;

public static class Telemetry
{
    public const string SourceName = "Ticketing.Api";

    /// <summary>
    /// Where our own spans come from. Plain .NET (System.Diagnostics): instrumenting code needs
    /// no OpenTelemetry package. OpenTelemetry is only how the spans get exported.
    /// </summary>
    public static readonly ActivitySource Source = new(SourceName);

    public static IServiceCollection AddTicketingTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("ticketing-api"))
            .WithTracing(tracing => tracing
                .AddSource(SourceName)
                .AddAspNetCoreInstrumentation(options =>
                    // Probes and lab plumbing would drown out the real traffic
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health") &&
                        !context.Request.Path.StartsWithSegments("/dev")));

        // Export over OTLP only when a collector is configured, e.g. the Aspire dashboard:
        //   OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        return services;
    }
}
```

Register it in `Program.cs`, after `AddTicketingHealth`:

```csharp
builder.Services.AddTicketingTelemetry(builder.Configuration);
```

### Checkpoint

**7 passing.**

### Optional: see your traces in the Aspire dashboard

If you have Docker, the Aspire dashboard is a free, standalone trace viewer:

```
docker run --rm -it -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` before `dotnet run`, then open http://localhost:18888. Same spans as the lab client's panel, in a real tool. Swap the endpoint for Application Insights, Honeycomb, Grafana, or any other OTLP backend and the code doesn't change.

---

## Step 3: Trace the store (10 min)

The request span tells you a request took 340 ms. It doesn't tell you whether the time went to the store, the cache, or your code. Add spans around the store calls with a **fourth decorator**. Create `src/Ticketing.Api/Data/TracingTicketStore.cs`:

```csharp
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
```

Put it in the chain in `Infrastructure/TicketingServiceExtensions.cs`. Inside the cache, so a cache hit never touches it:

```csharp
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


        return services;
    }
}
```

Now record cache hits and misses as **events** on the current span. In `Data/CachingTicketStore.cs`, replace `GetAsync` (and add `using System.Diagnostics;`):

```csharp
    public async Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var hit = true;
        var ticket = await cache.GetOrCreateAsync(
            Key(id),
            async token =>
            {
                hit = false;
                return await inner.GetAsync(id, token);
            },
            Entry,
            cancellationToken: cancellationToken);

        // Mark the current span, so a trace shows whether this read was served from cache
        Activity.Current?.AddEvent(new ActivityEvent(hit ? "cache.hit" : "cache.miss",
            tags: new ActivityTagsCollection { ["ticket.id"] = id.ToString() }));

        return ticket;
    }
```

### Checkpoint: one test is still red

Run the tests. **8 passing**, and `Store_calls_show_up_as_spans_inside_the_request` fails. Open the lab client, make the store slow, and open a ticket you haven't viewed. Look at its trace in the panel: a `cache.miss` event, 800 ms of time, and **no store span**. List requests show their store span fine. Only single-ticket reads lose it.

The store span exists. It's just in a **different trace**. `HybridCache` runs the factory outside the caller's async context, because one factory call may serve several callers who asked for the same key at the same moment. So `Activity.Current` inside the factory is empty, and the store span starts a brand-new trace. Your request's trace has an 800 ms gap with nothing in it, which is exactly the trace you'd be staring at during an incident.

### Fix it: re-attach the caller's span

Capture the caller's span before the cache call, and restore it inside the factory. The finished `CachingTicketStore.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

/// <summary>
/// Third decorator on the same interface. Single-ticket reads come from the cache;
/// every write evicts that ticket, so the next read goes back to the store.
///
/// It caches data, not HTTP responses, so authorization still runs on every request.
/// </summary>
public class CachingTicketStore(ITicketStore inner, HybridCache cache) : ITicketStore
{
    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private static string Key(Guid id) => $"ticket:{id}";

    public async Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var hit = true;
        var caller = Activity.Current;
        var ticket = await cache.GetOrCreateAsync(
            Key(id),
            async token =>
            {
                hit = false;
                // HybridCache runs this factory outside the caller's context (one call can serve
                // several concurrent callers), so the caller's span is lost. Re-attach it, or the
                // store span lands in a separate trace and this request's trace has a gap.
                Activity.Current = caller;
                return await inner.GetAsync(id, token);
            },
            Entry,
            cancellationToken: cancellationToken);

        // Mark the current span, so a trace shows whether this read was served from cache
        Activity.Current?.AddEvent(new ActivityEvent(hit ? "cache.hit" : "cache.miss",
            tags: new ActivityTagsCollection { ["ticket.id"] = id.ToString() }));

        return ticket;
    }

    // Lists vary by caller, filter, and page, and change whenever any ticket does. Not cached.
    public Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default) =>
        inner.ListAsync(cancellationToken);

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        inner.AddAsync(ticket, cancellationToken);

    public async Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        await inner.UpdateAsync(ticket, cancellationToken);
        await cache.RemoveAsync(Key(ticket.Id), cancellationToken);
    }

    public async Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default)
    {
        var added = await inner.AddCommentAsync(ticketId, comment, cancellationToken);
        await cache.RemoveAsync(Key(ticketId), cancellationToken);
        return added;
    }
}
```

Run the tests again: **9 passing**. In the trace panel, the store span now sits inside the request, under the 800 ms it accounts for.

> **The general lesson:** anything that runs your code on its own schedule (caches with stampede protection, background queues, `Task.Run`, timers) can break the trace. When a span is missing, look for the hand-off.

Notice the request spans are named like `GET /api/v{version:apiVersion}/tickets/{id:guid}`: the **route template**, not the URL. Naming spans by URL would create a distinct name per ticket ID, which tracing backends handle badly and bill heavily for.

---

## Step 4: Structured, correlated logs (10 min)

### Why the correlation test already passes

`An_error_can_be_found_in_the_logs_from_the_callers_trace_id` has been green all along. Send `/dev/boom` from `06-observability.http`, copy the `traceId` from the Problem, and find it in your console output:

```
fail: Ticketing.Api.Infrastructure.GlobalExceptionHandler[0] => SpanId:8ca2248a89472c27, TraceId:9d4de8c402b5978df89d734466e657d4, ...
```

ASP.NET Core attaches the current trace and span IDs to every log entry automatically. The `traceId` in Lab 2's Problem bodies **is** that trace ID. A caller quotes it, you search for it, and you have every log line and every span for their request. That's the correlation story, and you got it for free by using `Activity` consistently.

### Log events, not sentences

`"Ticket 1111 closed by dana"` is readable, and useless for queries. With structured logging, `TicketId` and `UserId` are **properties** your log backend can filter and group on: every ticket dana closed today, or every change to ticket 1111.

Create `src/Ticketing.Api/Features/Tickets/TicketLog.cs`:

```csharp
namespace Ticketing.Api.Features.Tickets;

/// <summary>
/// Structured log events for ticket changes. The [LoggerMessage] source generator writes the
/// implementations: no string formatting unless the level is enabled, no boxing, and every
/// {Placeholder} becomes a named property you can query on (TicketId, UserId), not just text.
/// </summary>
public partial class TicketLog(ILogger<TicketLog> logger)
{
    private readonly ILogger _logger = logger;

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Ticket {TicketId} created by {UserId} for {RequesterId}")]
    public partial void Created(Guid ticketId, string userId, string requesterId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Ticket {TicketId} assigned to {AssigneeId} by {UserId}")]
    public partial void Assigned(Guid ticketId, string assigneeId, string userId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Ticket {TicketId} closed by {UserId}")]
    public partial void Closed(Guid ticketId, string userId);
}
```

Register it in `AddTicketing()`, after the store:

```csharp
        services.AddSingleton<Features.Tickets.TicketLog>();
```

Inject it into the handlers and log after each change succeeds. `CloseTicket.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class CloseTicket
{
    public static RouteGroupBuilder MapCloseTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/close", Handle)
            .WithName("CloseTicket")
            .RequireAuthorization(Policies.Agent);
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, ClaimsPrincipal user, TicketLog log, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!ticket.CanMoveTo(TicketStatus.Closed))
            return Problems.InvalidTransition(ticket, "closed");

        ticket.Status = TicketStatus.Closed;
        await store.UpdateAsync(ticket, cancellationToken);
        log.Closed(ticket.Id, user.UserId());
        return TypedResults.Ok(ticket.ToResponse());
    }
}
```

Do the same in `AssignTicket.cs` (`log.Assigned(ticket.Id, request.AssigneeId, user.UserId());` after the update) and `CreateTicket.cs` (`log.Created(ticket.Id, user.UserId(), ticket.RequesterId);` after the add).

### Make the console show it

In `appsettings.json`, use the JSON console formatter with scopes, so every line is a machine-readable event carrying its trace ID:

```json
    "Console": {
      "FormatterName": "json",
      "FormatterOptions": {
        "IncludeScopes": true
      }
    }
```

(That goes inside the existing `"Logging"` section.) For readable output while you develop, override it in `appsettings.Development.json`:

```json
    "Console": {
      "FormatterName": "simple",
      "FormatterOptions": {
        "IncludeScopes": true,
        "SingleLine": true
      }
    }
```

### Checkpoint

**All 10 passing.**

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

**All green.** Run the API, make some requests from the lab client with the store slow, and read the traces. Then trigger `/dev/boom` and follow its `traceId` into the logs.

---

## Talk it over

### Metrics, the third signal

Traces explain one request. Logs explain one event. **Metrics** answer "how is it going overall?": requests per second, error rate, p95 latency, cache hit ratio. OpenTelemetry handles them the same way (`.WithMetrics(m => m.AddAspNetCoreInstrumentation())`), and a custom `Meter` can count tickets created or cache hits. Alerts are usually built on metrics, then you use traces and logs to investigate.

### Health checks: probe or SLO?

The store check here is close to an **SLO check**: it fails on slowness, not just on errors. That's powerful and easy to overdo. If a slow dependency makes every instance report unready at once, the load balancer has nowhere to send traffic, and a slowdown becomes an outage. Keep readiness about "can this instance serve at all," and let alerts handle "is it serving well."

### What to log

- **Log events with IDs, not narratives.** `TicketClosed {TicketId} {UserId}` beats a paragraph.
- **Never log secrets or personal data:** tokens, API keys, passwords, full request bodies.
- **Pick levels deliberately.** Information for business events, Warning for things that recovered, Error for things that didn't. If everything is Information, nothing is.
- **Sample traces in production.** Recording every span of every request gets expensive. Head sampling (keep 10%) and tail sampling (keep every error and every slow request) are both standard.
