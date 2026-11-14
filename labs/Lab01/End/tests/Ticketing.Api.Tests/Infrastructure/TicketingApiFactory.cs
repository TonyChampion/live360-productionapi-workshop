using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Ticketing.Api.Chaos;

namespace Ticketing.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real API in memory for tests. One instance per test class (IClassFixture).
/// </summary>
public class TicketingApiFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Turns off all fault injection. Call at the start of any test that cares.</summary>
    public async Task ResetChaosAsync()
    {
        using var client = CreateClient();
        var response = await client.PostAsync("/dev/chaos/reset", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetChaosAsync(ChaosState state)
    {
        using var client = CreateClient();
        var response = await client.PutAsJsonAsync("/dev/chaos", state, Json);
        response.EnsureSuccessStatusCode();
    }
}
