using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ticketing.Api.Chaos;

namespace Ticketing.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real API in memory for tests. One instance per test class (IClassFixture).
///
/// From Lab 3 on, the API requires a bearer token. Tests mint their own tokens with a
/// test signing key, so they never depend on dotnet user-jwts. CreateClient() signs in
/// as dana the agent; use CreateClientAs or CreateAnonymousClient for anything else.
/// </summary>
public class TicketingApiFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string TestIssuer = "ticketing-tests";
    private const string TestAudience = "ticketing-api";
    private static readonly SymmetricSecurityKey TestKey =
        new(Encoding.UTF8.GetBytes("ticketing-test-signing-key-at-least-32-bytes-long!"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Trust tokens signed with the test key (does nothing until the API adds JWT bearer auth)
        builder.ConfigureTestServices(services =>
            services.PostConfigureAll<JwtBearerOptions>(options =>
            {
                var p = options.TokenValidationParameters;
                p.IssuerSigningKeys = [TestKey];
                p.ValidIssuers = [TestIssuer];
                p.ValidAudiences = [TestAudience];
            }));
    }

    /// <summary>Every client signs in as dana the agent unless told otherwise.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("dana", "agent"));
    }

    /// <summary>A client signed in as a specific user with specific roles.</summary>
    public HttpClient CreateClientAs(string user, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user, roles));
        return client;
    }

    /// <summary>A client with no token at all.</summary>
    public HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    /// <summary>Mints a token with the same claims dotnet user-jwts uses: sub, unique_name, role.</summary>
    public static string Token(string user, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", user), new("unique_name", user) };
        claims.AddRange(roles.Select(r => new Claim("role", r)));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = TestIssuer,
            Audience = TestAudience,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(TestKey, SecurityAlgorithms.HmacSha256)
        });
    }

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
