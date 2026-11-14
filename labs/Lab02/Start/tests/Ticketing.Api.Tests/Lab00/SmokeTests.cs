using System.Net;
using System.Net.Http.Json;
using Ticketing.Api.Chaos;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab00;

[Trait(Lab.Trait, "0")]
public class SmokeTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    [Fact]
    public async Task Health_endpoint_responds()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Client_page_is_served()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Ticketing", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chaos_settings_can_be_changed_and_reset()
    {
        var client = factory.CreateClient();

        await factory.SetChaosAsync(new ChaosState(250, 0.5, true));
        var changed = await client.GetFromJsonAsync<ChaosState>("/dev/chaos", TicketingApiFactory.Json);

        await factory.ResetChaosAsync();
        var reset = await client.GetFromJsonAsync<ChaosState>("/dev/chaos", TicketingApiFactory.Json);

        Assert.Equal(new ChaosState(250, 0.5, true), changed);
        Assert.Equal(ChaosState.Off, reset);
    }

    [Fact]
    public async Task Seed_data_is_loaded()
    {
        await factory.ResetChaosAsync();
        var client = factory.CreateClient();

        var tickets = await client.GetFromJsonAsync<List<TicketDto>>("/api/v1/tickets", TicketingApiFactory.Json);

        Assert.NotNull(tickets);
        Assert.NotEmpty(tickets);
    }
}
