using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab04;

/// <summary>
/// The abuse surfaces: too many requests, and browsers on other sites.
/// </summary>
[Trait(Lab.Trait, "4")]
public class ProtectionTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string Problems = "https://ticketing.example/problems/";
    private const string AllowedOrigin = "https://support.ticketing.example";

    private static async Task<string?> ProblemType(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private static Task<HttpResponseMessage> CreateTicket(HttpClient client, string requester) =>
        client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Spam", description = "Again.", priority = "Low", requesterId = requester });

    // ---- Rate limiting ----

    [Fact]
    public async Task Creating_too_fast_is_429_with_retry_after()
    {
        var spammer = factory.CreateClientAs("spammer", "requester");
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? rejected = null;

        for (var i = 0; i < 25; i++)
        {
            var response = await CreateTicket(spammer, "spammer");
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { rejected = response; break; }
        }

        Assert.NotNull(rejected);
        Assert.Equal(20, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.NotNull(rejected!.Headers.RetryAfter);
        Assert.Equal(Problems + "rate-limited", await ProblemType(rejected));
    }

    [Fact]
    public async Task One_users_limit_does_not_block_another_user()
    {
        var greedy = factory.CreateClientAs("greedy", "requester");
        for (var i = 0; i < 25; i++)
            if ((await CreateTicket(greedy, "greedy")).StatusCode == HttpStatusCode.TooManyRequests) break;

        var patient = factory.CreateClientAs("patient", "requester");
        var response = await CreateTicket(patient, "patient");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Reads_are_not_rate_limited()
    {
        var reader = factory.CreateClientAs("reader", "requester");

        for (var i = 0; i < 40; i++)
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/tickets")).StatusCode);
    }

    // ---- Security headers ----

    [Fact]
    public async Task Api_responses_carry_security_headers()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/tickets");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Error_responses_carry_security_headers_too()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task The_lab_client_page_is_not_locked_down_like_the_api()
    {
        // A strict API CSP would block the client's own scripts and styles
        var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    // ---- CORS ----

    private static HttpRequestMessage Preflight(string origin) => new(HttpMethod.Options, "/api/v1/tickets")
    {
        Headers =
        {
            { "Origin", origin },
            { "Access-Control-Request-Method", "POST" },
            { "Access-Control-Request-Headers", "authorization,content-type" }
        }
    };

    [Fact]
    public async Task A_browser_on_the_support_site_may_call_the_api()
    {
        var response = await factory.CreateAnonymousClient().SendAsync(Preflight(AllowedOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task A_browser_on_any_other_site_may_not()
    {
        var response = await factory.CreateAnonymousClient().SendAsync(Preflight("https://evil.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
