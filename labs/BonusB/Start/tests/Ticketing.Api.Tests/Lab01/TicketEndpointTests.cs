using System.Net;
using System.Net.Http.Json;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab01;

/// <summary>
/// Written against the controller API. These must pass unchanged before, during,
/// and after the migration to minimal APIs: callers can't tell the implementation moved.
/// </summary>
[Trait(Lab.Trait, "1")]
public class TicketEndpointTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task List_returns_tickets()
    {
        var response = await _client.GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tickets = await response.Content.ReadFromJsonAsync<List<TicketDto>>(TicketingApiFactory.Json);
        Assert.Contains(tickets!, t => t.Id == SeedData.FirstTicketId);
    }

    [Fact]
    public async Task Get_returns_a_ticket()
    {
        var ticket = await _client.GetFromJsonAsync<TicketDto>(
            $"/api/v1/tickets/{SeedData.FirstTicketId}", TicketingApiFactory.Json);

        Assert.Equal("Cannot reset password", ticket!.Title);
        Assert.Equal("Open", ticket.Status);
    }

    [Fact]
    public async Task Get_unknown_ticket_returns_404()
    {
        var response = await _client.GetAsync($"/api/v1/tickets/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_returns_201_with_location()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Printer on fire", description = "Literally.", priority = "Urgent", requesterId = "bob" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);
        Assert.Equal("Printer on fire", created!.Title);
        Assert.Equal("Open", created.Status);
        Assert.Equal($"/api/v1/tickets/{created.Id}", response.Headers.Location!.AbsolutePath);

        var fetched = await _client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task Comments_can_be_listed_and_added()
    {
        var url = $"/api/v1/tickets/{SeedData.FirstTicketId}/comments";

        var add = await _client.PostAsJsonAsync(url, new { authorId = "dana", body = "Looking into it." });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var comments = await _client.GetFromJsonAsync<List<CommentDto>>(url, TicketingApiFactory.Json);
        Assert.Contains(comments!, c => c.Body == "Looking into it.");
    }

    [Fact]
    public async Task Comments_on_unknown_ticket_return_404()
    {
        var response = await _client.GetAsync($"/api/v1/tickets/{Guid.NewGuid()}/comments");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
