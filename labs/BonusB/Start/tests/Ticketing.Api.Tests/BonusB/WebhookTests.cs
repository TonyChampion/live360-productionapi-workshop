using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.BonusB;

public record SubscriptionDto(Guid Id, string Url, List<string> Events, DateTimeOffset CreatedAt, string? Secret);
public record DeliveryDto(string Id, string EventType, Guid TicketId, string Status, int? StatusCode, string? Error);

/// <summary>
/// Telling other systems: agents subscribe a URL, and every closed ticket is delivered there,
/// signed, in the background, retried when the receiver fails.
///
/// Every test runs its own copy of the app, with a fake receiver standing in for the network:
/// the "webhooks" HttpClient's last handler is replaced, so the resilience handler in front of it is real.
/// </summary>
[Trait(Lab.Trait, "B")]
public class WebhookTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string ReceiverUrl = "https://receiver.test/hooks";

    /// <summary>Stands in for the customer's server: records what arrives, answers what it's told to.</summary>
    private sealed class FakeReceiver : HttpMessageHandler
    {
        public record Received(Uri Url, Dictionary<string, string> Headers, string Body);

        public readonly ConcurrentQueue<Received> Requests = new();
        public Func<int, HttpStatusCode> Answer { get; set; } = _ => HttpStatusCode.OK;
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new Received(request.RequestUri!, headers, body));
            var attempt = Requests.Count;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new HttpResponseMessage(Answer(attempt));
        }

        public async Task<List<Received>> WaitFor(int count, int timeoutMs = 10_000)
        {
            var stopwatch = Stopwatch.StartNew();
            while (Requests.Count < count && stopwatch.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(25);
            Assert.True(Requests.Count >= count, $"Expected {count} deliveries, got {Requests.Count}");
            return Requests.ToList();
        }
    }

    private WebApplicationFactory<Program> App(FakeReceiver receiver) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Webhooks:RetryDelayMs", "10");
            builder.UseSetting("Jobs:RetryDelayMs", "50");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("webhooks").ConfigurePrimaryHttpMessageHandler(() => receiver));
        });

    private static async Task<HttpResponseMessage> Subscribe(HttpClient client, string url = ReceiverUrl, params string[] events) =>
        await client.PostAsJsonAsync("/api/v1/webhooks",
            new { url, events = events.Length == 0 ? ["ticket.closed"] : events });

    private static async Task<SubscriptionDto> Subscribed(HttpClient client)
    {
        var response = await Subscribe(client);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubscriptionDto>(TicketingApiFactory.Json))!;
    }

    private static async Task<Guid> CreateTicket(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Webhook test", description = "For a test.", priority = "Low", requesterId = "carol" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json))!.Id;
    }

    private static async Task Close(HttpClient client, Guid id) =>
        (await client.PostAsync($"/api/v1/tickets/{id}/close", null)).EnsureSuccessStatusCode();

    private static async Task<DeliveryDto> WaitForDelivery(HttpClient client, Guid subscriptionId, Func<DeliveryDto, bool> done)
    {
        var stopwatch = Stopwatch.StartNew();
        List<DeliveryDto>? deliveries = null;
        while (stopwatch.ElapsedMilliseconds < 10_000)
        {
            deliveries = await client.GetFromJsonAsync<List<DeliveryDto>>(
                $"/api/v1/webhooks/{subscriptionId}/deliveries", TicketingApiFactory.Json);
            if (deliveries!.FirstOrDefault(done) is { } delivery) return delivery;
            await Task.Delay(50);
        }
        throw new TimeoutException($"No matching delivery; saw {JsonSerializer.Serialize(deliveries)}");
    }

    /// <summary>Checks a signature the way a receiver would, from the spec, without the API's code.</summary>
    private static bool SignatureIsValid(string secret, FakeReceiver.Received delivery)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signed = $"{delivery.Headers["webhook-id"]}.{delivery.Headers["webhook-timestamp"]}.{delivery.Body}";
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signed)));
        return delivery.Headers["webhook-signature"].Split(' ').Contains(expected);
    }

    // ---- Subscriptions ----

    [Fact]
    public async Task Subscribing_returns_the_secret_once()
    {
        await using var app = App(new FakeReceiver());
        var client = app.CreateClient();

        var created = await Subscribe(client);
        var subscription = (await created.Content.ReadFromJsonAsync<SubscriptionDto>(TicketingApiFactory.Json))!;
        var listed = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/webhooks", TicketingApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        Assert.StartsWith("whsec_", subscription.Secret);
        var mine = Assert.Single(listed!);
        Assert.Equal(subscription.Id, mine.GetProperty("id").GetGuid());
        Assert.False(mine.TryGetProperty("secret", out _));   // never shown again
    }

    [Fact]
    public async Task Only_agents_manage_webhooks()
    {
        await using var app = App(new FakeReceiver());
        var alice = app.CreateClient();
        alice.DefaultRequestHeaders.Authorization = new("Bearer", TicketingApiFactory.Token("alice", "requester"));

        Assert.Equal(HttpStatusCode.Forbidden, (await Subscribe(alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/v1/webhooks")).StatusCode);
    }

    [Theory]
    [InlineData("not a url", "ticket.closed")]
    [InlineData("ftp://receiver.test/hooks", "ticket.closed")]
    [InlineData("/relative/hooks", "ticket.closed")]
    [InlineData(ReceiverUrl, "ticket.exploded")]
    public async Task Subscribing_checks_the_url_and_events(string url, string eventType)
    {
        await using var app = App(new FakeReceiver());

        var response = await Subscribe(app.CreateClient(), url, eventType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- Delivery ----

    [Fact]
    public async Task Closing_a_ticket_delivers_a_signed_webhook()
    {
        var receiver = new FakeReceiver();
        await using var app = App(receiver);
        var client = app.CreateClient();
        var subscription = await Subscribed(client);
        var ticketId = await CreateTicket(client);

        await Close(client, ticketId);
        var delivery = Assert.Single(await receiver.WaitFor(1));

        Assert.Equal(ReceiverUrl, delivery.Url.ToString());
        Assert.StartsWith("msg_", delivery.Headers["webhook-id"]);
        Assert.True(long.TryParse(delivery.Headers["webhook-timestamp"], out _));
        Assert.True(SignatureIsValid(subscription.Secret!, delivery), "signature doesn't verify");

        var body = JsonDocument.Parse(delivery.Body).RootElement;
        Assert.Equal("ticket.closed", body.GetProperty("type").GetString());
        Assert.Equal(ticketId, body.GetProperty("data").GetProperty("ticketId").GetGuid());
        Assert.Equal("dana", body.GetProperty("data").GetProperty("closedBy").GetString());

        var recorded = await WaitForDelivery(client, subscription.Id, d => d.Status == "Succeeded");
        Assert.Equal(delivery.Headers["webhook-id"], recorded.Id);
        Assert.Equal(200, recorded.StatusCode);
    }

    [Fact]
    public async Task Bulk_close_delivers_one_webhook_per_ticket()
    {
        var receiver = new FakeReceiver();
        await using var app = App(receiver);
        var client = app.CreateClient();
        await Subscribed(client);
        Guid[] ids = [await CreateTicket(client), await CreateTicket(client), await CreateTicket(client)];

        (await client.PostAsJsonAsync("/api/v1/tickets/bulk-close", new { ticketIds = ids })).EnsureSuccessStatusCode();
        var deliveries = await receiver.WaitFor(3);

        var delivered = deliveries.Select(d => JsonDocument.Parse(d.Body).RootElement
            .GetProperty("data").GetProperty("ticketId").GetGuid());
        Assert.Equal(ids.Order(), delivered.Order());
    }

    [Fact]
    public async Task Closing_does_not_wait_for_the_receiver()
    {
        var receiver = new FakeReceiver { Delay = TimeSpan.FromSeconds(3) };
        await using var app = App(receiver);
        var client = app.CreateClient();
        await Subscribed(client);
        var ticketId = await CreateTicket(client);

        var stopwatch = Stopwatch.StartNew();
        await Close(client, ticketId);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"Close took {stopwatch.ElapsedMilliseconds} ms");
        await receiver.WaitFor(1);   // ...and it was still delivered
    }

    [Fact]
    public async Task A_failing_receiver_is_retried_with_the_same_webhook_id()
    {
        // Fails twice, then works
        var receiver = new FakeReceiver { Answer = attempt => attempt <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK };
        await using var app = App(receiver);
        var client = app.CreateClient();
        var subscription = await Subscribed(client);

        await Close(client, await CreateTicket(client));
        var attempts = await receiver.WaitFor(3);

        // One event, three attempts: the same id each time, so the receiver can tell it's a repeat
        Assert.Single(attempts.Select(a => a.Headers["webhook-id"]).Distinct());
        Assert.All(attempts, a => Assert.True(SignatureIsValid(subscription.Secret!, a)));
        var recorded = await WaitForDelivery(client, subscription.Id, d => d.Status == "Succeeded");
        Assert.Equal(200, recorded.StatusCode);
    }

    [Fact]
    public async Task A_receiver_that_keeps_failing_is_recorded_as_failed()
    {
        var receiver = new FakeReceiver { Answer = _ => HttpStatusCode.InternalServerError };
        await using var app = App(receiver);
        var client = app.CreateClient();
        var subscription = await Subscribed(client);

        await Close(client, await CreateTicket(client));
        var recorded = await WaitForDelivery(client, subscription.Id, d => d.Status == "Failed");

        Assert.Equal(500, recorded.StatusCode);
        Assert.Equal(4, receiver.Requests.Count);   // the first try, plus 3 retries
    }

    [Fact]
    public async Task Unsubscribed_endpoints_get_nothing()
    {
        var receiver = new FakeReceiver();
        await using var app = App(receiver);
        var client = app.CreateClient();
        var subscription = await Subscribed(client);

        var deleted = await client.DeleteAsync($"/api/v1/webhooks/{subscription.Id}");
        await Close(client, await CreateTicket(client));
        await Task.Delay(500);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(receiver.Requests);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/webhooks/{subscription.Id}")).StatusCode);
    }
}
