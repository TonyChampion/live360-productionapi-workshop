using System.Net;
using System.Net.Http.Json;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab02;

/// <summary>
/// The happy paths for the endpoints Lab 2 hardens. Green from the start; they must stay green.
/// </summary>
[Trait(Lab.Trait, "2")]
public class TicketLifecycleTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task A_ticket_can_be_updated_assigned_and_closed()
    {
        var created = await (await _client.PostAsJsonAsync("/api/v1/tickets",
                new { title = "Lifecycle", description = "Start to finish.", priority = "Low", requesterId = "carol" }))
            .Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);
        var url = $"/api/v1/tickets/{created!.Id}";

        var updated = await (await _client.PatchAsJsonAsync(url, new { priority = "High" }))
            .Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);
        Assert.Equal("High", updated!.Priority);
        Assert.Equal("Lifecycle", updated.Title);

        var assignResponse = await _client.PostAsJsonAsync($"{url}/assign", new { assigneeId = "dana" });
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);
        var assigned = await assignResponse.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);
        Assert.Equal("dana", assigned!.AssigneeId);
        Assert.Equal("InProgress", assigned.Status);

        var closeResponse = await _client.PostAsync($"{url}/close", null);
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var closed = await closeResponse.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);
        Assert.Equal("Closed", closed!.Status);
    }
}
