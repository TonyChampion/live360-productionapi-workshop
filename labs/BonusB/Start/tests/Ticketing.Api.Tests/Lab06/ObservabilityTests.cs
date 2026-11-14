using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Ticketing.Api.Chaos;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab06;

/// <summary>
/// A production API reports on itself: health a load balancer can act on, traces that show
/// where time went, and structured logs you can find from a caller's traceId.
/// </summary>
[Trait(Lab.Trait, "6")]
public class ObservabilityTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<TicketDto> CreateTicketAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Observe me", description = "For a test.", priority = "Normal", requesterId = "carol" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json))!;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<HttpResponseMessage> WithChaos(ChaosState state, string url)
    {
        await factory.SetChaosAsync(state);
        try { return await factory.CreateAnonymousClient().GetAsync(url); }
        finally { await factory.ResetChaosAsync(); }
    }

    // ---- Health ----

    [Fact]
    public async Task Health_is_json_and_reports_each_check()
    {
        await factory.ResetChaosAsync();
        var response = await factory.CreateAnonymousClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var health = await Json(response);
        Assert.Equal("Healthy", health.GetProperty("status").GetString());
        Assert.Contains(health.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "ticket-store");
    }

    [Fact]
    public async Task A_slow_store_is_degraded_but_still_serving()
    {
        var response = await WithChaos(new ChaosState(700, 0, true), "/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", (await Json(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_down_store_is_unhealthy_with_no_internals()
    {
        var response = await WithChaos(new ChaosState(0, 0, Available: false), "/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("Exception", body);
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_the_store()
    {
        var response = await WithChaos(new ChaosState(0, 0, Available: false), "/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_follows_the_store()
    {
        var response = await WithChaos(new ChaosState(0, 0, Available: false), "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- Tracing ----

    [Fact]
    public void OpenTelemetry_tracing_is_registered()
    {
        Assert.NotNull(factory.Services.GetService<TracerProvider>());
    }

    [Fact]
    public async Task Store_calls_show_up_as_spans_inside_the_request()
    {
        var ticket = await CreateTicketAsync();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);

        await _client.GetAsync($"/api/v1/tickets/{ticket.Id}");   // cache miss: goes to the store

        var storeSpan = spans.SingleOrDefault(a =>
            a.Source.Name == "Ticketing.Api" && Equals(a.GetTagItem("ticket.id"), ticket.Id.ToString()));
        Assert.NotNull(storeSpan);

        // The request span stops just after the response reaches the client, so give it a moment
        var request = await WaitFor(() => spans.FirstOrDefault(a =>
            a.Source.Name == "Microsoft.AspNetCore" && a.TraceId == storeSpan!.TraceId));
        Assert.NotNull(request);
        // Request spans are named by route template, not URL, so IDs don't explode the span names
        Assert.Contains("tickets/{id", request!.DisplayName);
    }

    [Fact]
    public async Task A_cache_hit_shows_in_the_trace_and_skips_the_store()
    {
        var ticket = await CreateTicketAsync();
        await _client.GetAsync($"/api/v1/tickets/{ticket.Id}");   // warm the cache

        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await _client.GetAsync($"/api/v1/tickets/{ticket.Id}");
        await WaitFor(() => spans.FirstOrDefault(a => a.Source.Name == "Microsoft.AspNetCore"));

        Assert.DoesNotContain(spans, a =>
            a.Source.Name == "Ticketing.Api" && Equals(a.GetTagItem("ticket.id"), ticket.Id.ToString()));
        Assert.Contains(spans.SelectMany(a => a.Events), e =>
            e.Name == "cache.hit" && e.Tags.Any(t => t.Key == "ticket.id" && Equals(t.Value, ticket.Id.ToString())));
    }

    private static async Task<T?> WaitFor<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 40; i++)
        {
            if (find() is { } found) return found;
            await Task.Delay(25);
        }
        return null;
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> into)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Ticketing.Api" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // ---- Structured, correlated logs ----

    private (HttpClient client, LogCapture logs) ClientWithLogCapture()
    {
        var logs = new LogCapture();
        var app = factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
        return (app.CreateClient(), logs);
    }

    [Fact]
    public async Task Closing_a_ticket_writes_a_structured_log_entry()
    {
        var (client, logs) = ClientWithLogCapture();
        var created = await (await client.PostAsJsonAsync("/api/v1/tickets",
                new { title = "Log me", description = "x", priority = "Low", requesterId = "carol" }))
            .Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);

        await client.PostAsync($"/api/v1/tickets/{created!.Id}/close", null);

        var entry = Assert.Single(logs.Entries, e => e.Properties.ContainsKey("TicketId")
                                                     && Equals(e.Properties["TicketId"], created.Id)
                                                     && e.Message.Contains("closed"));
        Assert.Equal("dana", entry.Properties["UserId"]);
    }

    [Fact]
    public async Task An_error_can_be_found_in_the_logs_from_the_callers_trace_id()
    {
        var (client, logs) = ClientWithLogCapture();

        var problem = await Json(await client.GetAsync("/dev/boom"));
        var traceparent = problem.GetProperty("traceId").GetString()!;   // 00-<trace id>-<span id>-01
        var traceId = traceparent.Split('-')[1];

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error
                                           && e.Scopes.TryGetValue("TraceId", out var id)
                                           && id?.ToString() == traceId);
    }
}
