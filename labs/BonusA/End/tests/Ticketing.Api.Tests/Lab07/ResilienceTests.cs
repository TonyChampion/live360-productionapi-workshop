using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Ticketing.Api.Chaos;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab07;

/// <summary>
/// Failing on purpose: every dependency failure leaves through a designed exit, within a known
/// time budget, without retrying what isn't safe to retry, and without hammering a store that's down.
///
/// Each test runs in its own copy of the app (its own store, chaos settings, and circuit breaker),
/// with a small, fast timeout budget, so breaker state never leaks between tests.
/// </summary>
[Trait(Lab.Trait, "7")]
public class ResilienceTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string Problems = "https://ticketing.example/problems/";

    private static readonly Dictionary<string, string> FastBudget = new()
    {
        ["Resilience:RequestTimeoutMs"] = "3000",
        ["Resilience:StoreTotalTimeoutMs"] = "800",
        ["Resilience:StoreAttemptTimeoutMs"] = "300",
        ["Resilience:StoreMaxRetries"] = "2",
        ["Resilience:StoreRetryDelayMs"] = "20",
    };

    private static readonly Dictionary<string, string> TwitchyBreaker = new(FastBudget)
    {
        ["Resilience:StoreMaxRetries"] = "1",
        ["Resilience:BreakerMinimumThroughput"] = "2",
        ["Resilience:BreakerFailureRatio"] = "0.5",
        ["Resilience:BreakerSamplingSeconds"] = "5",
        ["Resilience:BreakDurationSeconds"] = "30",
    };

    private WebApplicationFactory<Program> App(Dictionary<string, string> settings, params (string, string)[] overrides) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        });

    private static Task Chaos(HttpClient client, ChaosState state) =>
        client.PutAsJsonAsync("/dev/chaos", state, TicketingApiFactory.Json);

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> ProblemType(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await Body(response)).GetProperty("type").GetString()!;
    }

    // ---- Spans, to see what actually happened to the store ----

    private static ActivityListener Listen(ConcurrentQueue<Activity> into)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Ticketing.Api",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>The store calls made while handling the request that produced this Problem.</summary>
    private static async Task<List<Activity>> StoreCallsFor(HttpResponseMessage problemResponse, ConcurrentQueue<Activity> spans)
    {
        var traceparent = (await Body(problemResponse)).GetProperty("traceId").GetString()!;
        var traceId = traceparent.Split('-')[1];
        await Task.Delay(50);   // let in-flight spans finish
        return spans.Where(s => s.TraceId.ToString() == traceId && s.DisplayName.StartsWith("ticket-store")).ToList();
    }

    // ---- Retries ----

    [Fact]
    public async Task Two_transient_failures_are_retried_away()
    {
        using var app = App(FastBudget);
        var client = app.CreateClient();
        await Chaos(client, new ChaosState(0, 0, true, FailNextCalls: 2));

        var response = await client.GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Persistent_transient_failures_are_503_not_500()
    {
        using var app = App(FastBudget);
        var client = app.CreateClient();
        await Chaos(client, new ChaosState(0, ErrorRate: 1, true));

        var response = await client.GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Problems + "store-unavailable", await ProblemType(response));
    }

    [Fact]
    public async Task A_down_store_is_503_and_is_not_retried()
    {
        using var app = App(FastBudget);
        var client = app.CreateClient();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await Chaos(client, new ChaosState(0, 0, Available: false));

        var response = await client.GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Problems + "store-unavailable", await ProblemType(response));
        Assert.Single(await StoreCallsFor(response, spans));
    }

    // ---- Timeouts ----

    [Fact]
    public async Task A_hung_read_is_retried_then_cut_off_at_504_within_the_budget()
    {
        using var app = App(FastBudget);
        var client = app.CreateClient();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await Chaos(client, new ChaosState(LatencyMs: 5000, 0, true));

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/api/v1/tickets");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(Problems + "timeout", await ProblemType(response));
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Took {stopwatch.ElapsedMilliseconds} ms; the budget is 800 ms");
        Assert.True((await StoreCallsFor(response, spans)).Count >= 2, "A timed-out read should be retried");
    }

    [Fact]
    public async Task A_timed_out_write_is_not_retried()
    {
        using var app = App(FastBudget);
        var client = app.CreateClient();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await Chaos(client, new ChaosState(LatencyMs: 4000, 0, true));

        var response = await client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Maybe saved?", description = "x", priority = "Low", requesterId = "carol" });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Single(await StoreCallsFor(response, spans));
    }

    [Fact]
    public void An_impossible_timeout_budget_fails_at_startup()
    {
        // The store's budget must fit inside the request's budget
        using var app = App(FastBudget, ("Resilience:StoreTotalTimeoutMs", "5000"));

        var failure = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        var invalid = Assert.IsType<OptionsValidationException>(failure.GetBaseException());
        Assert.Contains("Resilience", invalid.Message);
    }

    // ---- Cancellation ----

    [Fact]
    public async Task A_disconnected_client_cancels_the_write()
    {
        using var app = App(FastBudget, ("Resilience:StoreTotalTimeoutMs", "2500"), ("Resilience:StoreAttemptTimeoutMs", "2500"));
        var client = app.CreateClient();
        await Chaos(client, new ChaosState(LatencyMs: 1500, 0, true));
        var title = $"Ghost {Guid.NewGuid()}";

        using var hangUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsJsonAsync("/api/v1/tickets",
            new { title, description = "x", priority = "Low", requesterId = "carol" }, hangUp.Token));

        await Task.Delay(1800);   // long enough for the write to land if it wasn't cancelled
        await Chaos(client, ChaosState.Off);
        var tickets = await client.GetFromJsonAsync<List<TicketDto>>("/api/v1/tickets?pageSize=100", TicketingApiFactory.Json);
        Assert.DoesNotContain(tickets!, t => t.Title == title);
    }

    // ---- Circuit breaker ----

    /// <summary>Fails store calls until the breaker opens. Returns the first fail-fast response.</summary>
    private static async Task<HttpResponseMessage?> OpenTheCircuit(HttpClient client)
    {
        await Chaos(client, new ChaosState(0, ErrorRate: 1, true));
        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/api/v1/tickets");
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null)
                return response;
        }
        return null;
    }

    [Fact]
    public async Task After_repeated_failures_the_circuit_opens_and_fails_fast()
    {
        using var app = App(TwitchyBreaker);
        var client = app.CreateClient();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);

        var failFast = await OpenTheCircuit(client);

        Assert.NotNull(failFast);
        Assert.Equal(Problems + "store-unavailable", await ProblemType(failFast!));
        Assert.Empty(await StoreCallsFor(failFast!, spans));   // the store wasn't even called
    }

    [Fact]
    public async Task An_open_circuit_reports_degraded_health_and_cached_reads_still_work()
    {
        using var app = App(TwitchyBreaker);
        var client = app.CreateClient();
        var ticket = $"/api/v1/tickets/{SeedData.FirstTicketId}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ticket)).StatusCode);   // warm the cache

        Assert.NotNull(await OpenTheCircuit(client));
        var health = await client.GetAsync("/health");
        var cached = await client.GetAsync(ticket);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var report = await Body(health);
        Assert.Equal("Degraded", report.GetProperty("status").GetString());
        Assert.Contains("circuit", report.GetProperty("checks")[0].GetProperty("description").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
    }

    [Fact]
    public async Task The_circuit_closes_again_when_the_store_recovers()
    {
        using var app = App(TwitchyBreaker, ("Resilience:BreakDurationSeconds", "1"));
        var client = app.CreateClient();
        Assert.NotNull(await OpenTheCircuit(client));

        await Chaos(client, ChaosState.Off);
        await Task.Delay(1200);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal("Healthy", (await Body(await client.GetAsync("/health"))).GetProperty("status").GetString());
    }
}
