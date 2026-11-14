using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Ticketing.Api.Chaos;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.BonusA;

/// <summary>
/// Safe to retry: a create sent twice with the same Idempotency-Key makes one ticket, and the
/// second call gets the first call's answer. Tests that change timings run in their own copy of the app.
/// </summary>
[Trait(Lab.Trait, "A")]
public class IdempotencyTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string Problems = "https://ticketing.example/problems/";
    private const string KeyHeader = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotent-Replayed";

    private static object NewTicket(string title = "Printer on fire") =>
        new { title, description = "Third floor.", priority = "High", requesterId = "carol" };

    private static string NewKey() => Guid.NewGuid().ToString();

    private static Task<HttpResponseMessage> Create(HttpClient client, object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets") { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add(KeyHeader, key);
        return client.SendAsync(request);
    }

    private static async Task<TicketDto> Ticket(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json))!;

    private static async Task<int> TicketCount(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/tickets?pageSize=1");
        response.EnsureSuccessStatusCode();
        return int.Parse(response.Headers.GetValues("X-Total-Count").Single());
    }

    private static async Task<string> ProblemType(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("type").GetString()!;
    }

    private static bool Replayed(HttpResponseMessage response) =>
        response.Headers.TryGetValues(ReplayedHeader, out var values) && values.Single() == "true";

    // ---- The key in the data ----

    [Fact]
    public async Task Same_key_twice_creates_one_ticket()
    {
        var client = factory.CreateClient();
        var before = await TicketCount(client);
        var key = NewKey();

        var first = await Create(client, NewTicket(), key);
        var second = await Create(client, NewTicket(), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal((await Ticket(first)).Id, (await Ticket(second)).Id);
        Assert.Equal(before + 1, await TicketCount(client));
    }

    [Fact]
    public async Task Ticket_created_with_a_key_is_readable_right_away()
    {
        var client = factory.CreateClient();

        var created = await Create(client, NewTicket(), NewKey());
        var read = await client.GetAsync(created.Headers.Location);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Without_a_key_every_create_is_a_new_ticket()
    {
        var client = factory.CreateClient();

        var first = await Create(client, NewTicket(), key: null);
        var second = await Create(client, NewTicket(), key: null);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual((await Ticket(first)).Id, (await Ticket(second)).Id);
        Assert.False(Replayed(second));
    }

    [Fact]
    public async Task Keys_belong_to_the_caller()
    {
        var dana = factory.CreateClient();
        var erin = factory.CreateClientAs("erin", "agent");
        var key = NewKey();

        var danas = await Create(dana, NewTicket(), key);
        var erins = await Create(erin, NewTicket(), key);

        Assert.Equal(HttpStatusCode.Created, erins.StatusCode);
        Assert.NotEqual((await Ticket(danas)).Id, (await Ticket(erins)).Id);
        Assert.False(Replayed(erins));
    }

    // ---- The response cache in front ----

    [Fact]
    public async Task Replay_returns_the_original_response_and_says_so()
    {
        var client = factory.CreateClient();
        var key = NewKey();

        var first = await Create(client, NewTicket(), key);
        var second = await Create(client, NewTicket(), key);

        Assert.False(Replayed(first));
        Assert.True(Replayed(second));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_422()
    {
        var client = factory.CreateClient();
        var key = NewKey();

        await Create(client, NewTicket("Printer on fire"), key);
        var reused = await Create(client, NewTicket("Coffee machine on fire"), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(Problems + "idempotency-key-reused", await ProblemType(reused));
    }

    [Fact]
    public async Task Blank_key_is_400()
    {
        // (HttpClient drops a header with an empty value before it's sent, so blank stands in for empty)
        var response = await Create(factory.CreateClient(), NewTicket(), "   ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Problems + "idempotency-key-invalid", await ProblemType(response));
    }

    [Fact]
    public async Task Overlong_key_is_400()
    {
        var response = await Create(factory.CreateClient(), NewTicket(), new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Problems + "idempotency-key-invalid", await ProblemType(response));
    }

    [Fact]
    public async Task A_failed_create_does_not_use_up_the_key()
    {
        await using var app = factory.WithWebHostBuilder(_ => { });
        var client = app.CreateClient();
        var key = NewKey();

        await client.PutAsJsonAsync("/dev/chaos", ChaosState.Off with { Available = false }, TicketingApiFactory.Json);
        var failed = await Create(client, NewTicket(), key);
        await client.PostAsync("/dev/chaos/reset", null);
        var retried = await Create(client, NewTicket(), key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(Replayed(retried));
    }

    [Fact]
    public async Task Same_key_while_the_first_is_still_running_is_409()
    {
        // A slow store, with a timeout budget big enough that the slow call still succeeds
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Resilience:RequestTimeoutMs", "10000");
            builder.UseSetting("Resilience:StoreTotalTimeoutMs", "8000");
            builder.UseSetting("Resilience:StoreAttemptTimeoutMs", "4000");
        });
        var client = app.CreateClient();
        await client.PutAsJsonAsync("/dev/chaos", ChaosState.Off with { LatencyMs = 1000 }, TicketingApiFactory.Json);
        var key = NewKey();

        var slow = Create(client, NewTicket(), key);
        await Task.Delay(300);
        var meanwhile = await Create(client, NewTicket(), key);

        Assert.Equal(HttpStatusCode.Conflict, meanwhile.StatusCode);
        Assert.Equal(Problems + "idempotency-key-in-progress", await ProblemType(meanwhile));
        Assert.Equal(HttpStatusCode.Created, (await slow).StatusCode);
    }

    [Fact]
    public async Task After_the_key_expires_a_retry_still_makes_no_duplicate()
    {
        // The response cache forgets after a second; the ticket id derived from the key doesn't
        await using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("Idempotency:ExpirySeconds", "1"));
        var client = app.CreateClient();
        var key = NewKey();

        var first = await Create(client, NewTicket(), key);
        var before = await TicketCount(client);
        await Task.Delay(1500);
        var late = await Create(client, NewTicket(), key);

        Assert.Equal(HttpStatusCode.Created, late.StatusCode);
        Assert.False(Replayed(late));   // not from the cache...
        Assert.Equal((await Ticket(first)).Id, (await Ticket(late)).Id);   // ...but still the same ticket
        Assert.Equal(before, await TicketCount(client));
    }
}
