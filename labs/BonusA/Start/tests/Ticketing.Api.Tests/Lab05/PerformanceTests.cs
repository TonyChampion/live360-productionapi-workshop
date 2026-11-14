using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ticketing.Api.Chaos;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab05;

/// <summary>
/// Make it fast, keep it right: bounded lists, cached reads that stay correct and secure,
/// and ETags so callers can skip downloads and avoid overwriting each other's changes.
/// </summary>
[Trait(Lab.Trait, "5")]
public class PerformanceTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string Problems = "https://ticketing.example/problems/";
    private static readonly string AliceTicket = $"/api/v1/tickets/{SeedData.FirstTicketId}";
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<TicketDto> CreateTicketAsync(string title = "Perf test")
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title, description = "For a test.", priority = "Normal", requesterId = "carol" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json))!;
    }

    private static async Task<string?> ProblemType(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    // ---- Pagination and filtering ----

    [Fact]
    public async Task Lists_are_bounded_and_report_the_total()
    {
        var response = await _client.GetAsync("/api/v1/tickets?pageSize=3");

        var tickets = await response.Content.ReadFromJsonAsync<List<TicketDto>>(TicketingApiFactory.Json);
        Assert.Equal(3, tickets!.Count);
        var total = int.Parse(response.Headers.GetValues("X-Total-Count").Single());
        Assert.True(total > 3);
    }

    [Fact]
    public async Task Walking_every_page_returns_each_ticket_exactly_once()
    {
        var seen = new List<Guid>();
        int total = 0;

        for (var page = 1; page <= 50; page++)
        {
            var response = await _client.GetAsync($"/api/v1/tickets?page={page}&pageSize=4");
            total = int.Parse(response.Headers.GetValues("X-Total-Count").Single());
            var tickets = await response.Content.ReadFromJsonAsync<List<TicketDto>>(TicketingApiFactory.Json);
            if (tickets!.Count == 0) break;
            seen.AddRange(tickets.Select(t => t.Id));
        }

        Assert.Equal(total, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_link_header_points_to_the_next_page_until_there_is_none()
    {
        var first = await _client.GetAsync("/api/v1/tickets?page=1&pageSize=4");
        var total = int.Parse(first.Headers.GetValues("X-Total-Count").Single());
        var lastPage = (total + 3) / 4;
        var last = await _client.GetAsync($"/api/v1/tickets?page={lastPage}&pageSize=4");

        Assert.Contains("rel=\"next\"", first.Headers.GetValues("Link").Single());
        Assert.Contains("page=2", first.Headers.GetValues("Link").Single());
        Assert.False(last.Headers.TryGetValues("Link", out var links) && links.Single().Contains("rel=\"next\""));
    }

    [Fact]
    public async Task Lists_can_be_filtered()
    {
        var tickets = await _client.GetFromJsonAsync<List<TicketDto>>(
            "/api/v1/tickets?status=Open&pageSize=100", TicketingApiFactory.Json);

        Assert.NotEmpty(tickets!);
        Assert.All(tickets!, t => Assert.Equal("Open", t.Status));
    }

    [Fact]
    public async Task An_oversized_page_is_a_validation_problem()
    {
        var response = await _client.GetAsync("/api/v1/tickets?pageSize=500");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Problems + "validation", await ProblemType(response));
    }

    // ---- Caching ----

    [Fact]
    public async Task A_ticket_read_once_is_served_from_cache_even_when_the_store_is_down()
    {
        await factory.ResetChaosAsync();
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(AliceTicket)).StatusCode);

        try
        {
            await factory.SetChaosAsync(new ChaosState(0, 0, Available: false));
            var cached = await _client.GetAsync(AliceTicket);
            Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        }
        finally
        {
            await factory.ResetChaosAsync();
        }
    }

    [Fact]
    public async Task Changing_a_ticket_invalidates_its_cache_entry()
    {
        var ticket = await CreateTicketAsync();
        var url = $"/api/v1/tickets/{ticket.Id}";
        await _client.GetAsync(url);

        await _client.PatchAsJsonAsync(url, new { title = "Renamed" });
        await _client.PostAsJsonAsync($"{url}/comments", new { authorId = "dana", body = "Note" });
        var after = JsonDocument.Parse(await _client.GetStringAsync(url)).RootElement;

        Assert.Equal("Renamed", after.GetProperty("title").GetString());
        Assert.Equal(1, after.GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public async Task Caching_never_bypasses_authorization()
    {
        await factory.CreateClientAs("dana", "agent").GetAsync(AliceTicket);   // now cached

        var response = await factory.CreateClientAs("bob", "requester").GetAsync(AliceTicket);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- ETags ----

    [Fact]
    public async Task A_ticket_has_an_etag()
    {
        var response = await _client.GetAsync(AliceTicket);

        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task An_unchanged_ticket_returns_304_with_no_body()
    {
        var first = await _client.GetAsync(AliceTicket);
        var request = new HttpRequestMessage(HttpMethod.Get, AliceTicket);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        var second = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_changed_ticket_returns_200_with_a_new_etag()
    {
        var ticket = await CreateTicketAsync();
        var url = $"/api/v1/tickets/{ticket.Id}";
        var before = (await _client.GetAsync(url)).Headers.ETag!;
        await _client.PatchAsJsonAsync(url, new { priority = "High" });

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfNoneMatch.Add(before);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(before, response.Headers.ETag);
    }

    [Fact]
    public async Task Updating_with_a_stale_etag_is_412_so_changes_are_not_lost()
    {
        var ticket = await CreateTicketAsync();
        var url = $"/api/v1/tickets/{ticket.Id}";
        var etag = (await _client.GetAsync(url)).Headers.ETag!;

        // Someone else changes the ticket first
        await _client.PatchAsJsonAsync(url, new { priority = "Urgent" });

        // Our update was based on the old version
        var stale = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(new { priority = "Low" })
        };
        stale.Headers.IfMatch.Add(etag);
        var response = await _client.SendAsync(stale);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(Problems + "precondition-failed", await ProblemType(response));
    }

    [Fact]
    public async Task Updating_with_the_current_etag_succeeds_and_returns_the_new_one()
    {
        var ticket = await CreateTicketAsync();
        var url = $"/api/v1/tickets/{ticket.Id}";
        var etag = (await _client.GetAsync(url)).Headers.ETag!;

        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(new { priority = "Low" })
        };
        request.Headers.IfMatch.Add(etag);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotEqual(etag, response.Headers.ETag);
    }
}
