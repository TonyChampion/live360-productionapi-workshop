using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab03;

/// <summary>
/// Who's calling, and what can they do? Seed data: alice owns the first ticket; dana is an agent.
///   401: we don't know who you are
///   403: we know who you are, and you're not allowed
/// </summary>
[Trait(Lab.Trait, "3")]
public class AuthTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string Problems = "https://ticketing.example/problems/";
    private static readonly string AliceTicket = $"/api/v1/tickets/{SeedData.FirstTicketId}";

    private HttpClient Alice => factory.CreateClientAs("alice", "requester");
    private HttpClient Bob => factory.CreateClientAs("bob", "requester");
    private HttpClient Dana => factory.CreateClientAs("dana", "agent");
    private HttpClient Admin => factory.CreateClientAs("ari", "admin");

    private static async Task<string?> ProblemType(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private async Task<Guid> CreateTicketFor(HttpClient client, string requester)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Auth test", description = "For a test.", priority = "Normal", requesterId = requester });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json))!.Id;
    }

    // ---- 401: no identity ----

    [Fact]
    public async Task No_token_is_401_with_a_problem_and_a_challenge()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Equal(Problems + "unauthorized", await ProblemType(response));
    }

    [Fact]
    public async Task A_forged_token_is_401()
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not.a.real-token");

        var response = await client.GetAsync("/api/v1/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_and_the_client_stay_public()
    {
        var anonymous = factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/")).StatusCode);
    }

    // ---- Ownership: requesters see their own tickets, agents see all ----

    [Fact]
    public async Task A_requester_can_read_their_own_ticket()
    {
        var response = await Alice.GetAsync(AliceTicket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_requester_cannot_read_someone_elses_ticket()
    {
        var response = await Bob.GetAsync(AliceTicket);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(Problems + "forbidden", await ProblemType(response));
    }

    [Fact]
    public async Task An_agent_can_read_any_ticket()
    {
        var response = await Dana.GetAsync(AliceTicket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_requester_only_lists_their_own_tickets()
    {
        var tickets = await Bob.GetFromJsonAsync<List<TicketDto>>("/api/v1/tickets", TicketingApiFactory.Json);

        Assert.NotEmpty(tickets!);
        Assert.All(tickets!, t => Assert.Equal("bob", t.RequesterId));
    }

    [Fact]
    public async Task A_requester_cannot_comment_on_someone_elses_ticket()
    {
        var response = await Bob.PostAsJsonAsync($"{AliceTicket}/comments", new { authorId = "bob", body = "Hi" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_requester_cannot_update_someone_elses_ticket()
    {
        var response = await Bob.PatchAsJsonAsync(AliceTicket, new { title = "Mine now" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Creating: for yourself, or for anyone if you're an agent ----

    [Fact]
    public async Task A_requester_can_create_a_ticket_for_themselves()
    {
        await CreateTicketFor(Bob, "bob");
    }

    [Fact]
    public async Task A_requester_cannot_create_a_ticket_for_someone_else()
    {
        var response = await Bob.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Framing alice", description = "Hmm.", priority = "Low", requesterId = "alice" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_agent_can_create_a_ticket_on_someones_behalf()
    {
        await CreateTicketFor(Dana, "carol");
    }

    // ---- Agent-only actions ----

    [Fact]
    public async Task A_requester_cannot_assign_even_their_own_ticket()
    {
        var id = await CreateTicketFor(Bob, "bob");

        var response = await Bob.PostAsJsonAsync($"/api/v1/tickets/{id}/assign", new { assigneeId = "bob" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_requester_cannot_close_even_their_own_ticket()
    {
        var id = await CreateTicketFor(Bob, "bob");

        var response = await Bob.PostAsync($"/api/v1/tickets/{id}/close", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_do_what_an_agent_can()
    {
        var id = await CreateTicketFor(Dana, "carol");

        var response = await Admin.PostAsync($"/api/v1/tickets/{id}/close", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
