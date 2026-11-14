using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab03;

/// <summary>
/// Callers that aren't people: a service sends X-Api-Key instead of a bearer token,
/// and becomes a ClaimsPrincipal with the roles configured for its key.
/// </summary>
[Trait(Lab.Trait, "3")]
public class ApiKeyTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string Problems = "https://ticketing.example/problems/";
    private const string TestApiKey = "test-integration-key-0123456789";

    private static async Task<string?> ProblemType(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private HttpClient ApiKeyClient(string key)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ApiKeys:test-integration:Key", TestApiKey);
            b.UseSetting("ApiKeys:test-integration:Roles:0", "agent");
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;   // no bearer token, only the key
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task A_valid_api_key_authenticates_a_service_caller()
    {
        var response = await ApiKeyClient(TestApiKey).GetAsync($"/api/v1/tickets/{SeedData.FirstTicketId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_api_key_gets_the_roles_configured_for_it()
    {
        // test-integration is configured as an agent, so it can close tickets.
        // WithWebHostBuilder starts a separate app with its own in-memory store,
        // so use a seeded ticket rather than one created through the main factory.
        var response = await ApiKeyClient(TestApiKey).PostAsync($"/api/v1/tickets/{SeedData.FirstTicketId}/close", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_api_key_is_401()
    {
        var response = await ApiKeyClient("guessed-key").GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Problems + "unauthorized", await ProblemType(response));
    }
}
