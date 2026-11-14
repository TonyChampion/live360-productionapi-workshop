using System.Net;
using System.Net.Http.Json;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab02;

/// <summary>
/// Every failure leaves through a designed exit: the right status code, a Problem Details
/// body with a type from the catalog, and a trace ID the caller can hand to support.
/// </summary>
[Trait(Lab.Trait, "2")]
public class ErrorContractTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> CreateTicketAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Test ticket", description = "For a test.", priority = "Normal", requesterId = "bob" });
        var ticket = await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);
        return ticket!.Id;
    }

    // ---- 400: the request itself is wrong ----

    [Fact]
    public async Task Create_without_a_title_is_a_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "", description = "No title.", priority = "Normal", requesterId = "bob" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await Problem.ReadAsync(response);
        Assert.Equal(Problem.Validation, problem.Str("type"));
        Assert.NotEmpty(problem.ErrorsFor("title"));
    }

    [Fact]
    public async Task Create_with_an_overlong_title_is_a_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = new string('x', 201), description = "Too long.", priority = "Normal", requesterId = "bob" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Problem.ReadAsync(response)).ErrorsFor("title"));
    }

    [Fact]
    public async Task Create_without_a_requester_is_a_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "No requester", description = "Who asked?", priority = "Normal", requesterId = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Problem.ReadAsync(response)).ErrorsFor("requesterId"));
    }

    [Fact]
    public async Task Create_with_an_unknown_priority_is_a_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Bad priority", description = "Hmm.", priority = "Critical", requesterId = "bob" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await Problem.ReadAsync(response);
    }

    [Fact]
    public async Task Comment_without_a_body_is_a_validation_problem()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tickets/{SeedData.FirstTicketId}/comments", new { authorId = "dana", body = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Problem.ReadAsync(response)).ErrorsFor("body"));
    }

    [Fact]
    public async Task Update_to_an_empty_title_is_a_validation_problem()
    {
        var id = await CreateTicketAsync();

        var response = await _client.PatchAsJsonAsync($"/api/v1/tickets/{id}", new { title = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Problem.ReadAsync(response)).ErrorsFor("title"));
    }

    [Fact]
    public async Task Assign_without_an_assignee_is_a_validation_problem()
    {
        var id = await CreateTicketAsync();

        var response = await _client.PostAsJsonAsync($"/api/v1/tickets/{id}/assign", new { assigneeId = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Problem.ReadAsync(response)).ErrorsFor("assigneeId"));
    }

    // ---- 404: the thing doesn't exist ----

    [Fact]
    public async Task Unknown_ticket_is_a_ticket_not_found_problem()
    {
        var id = Guid.NewGuid();

        var response = await _client.GetAsync($"/api/v1/tickets/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await Problem.ReadAsync(response);
        Assert.Equal(Problem.TicketNotFound, problem.Str("type"));
        Assert.Contains(id.ToString(), problem.Str("detail"));
    }

    [Fact]
    public async Task Closing_an_unknown_ticket_is_a_ticket_not_found_problem()
    {
        var response = await _client.PostAsync($"/api/v1/tickets/{Guid.NewGuid()}/close", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(Problem.TicketNotFound, (await Problem.ReadAsync(response)).Str("type"));
    }

    [Fact]
    public async Task Unknown_route_is_still_a_problem()
    {
        var response = await _client.GetAsync("/api/v2/tickets");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(Problem.NotFound, (await Problem.ReadAsync(response)).Str("type"));
    }

    // ---- 409: the request is fine, but the ticket's state won't allow it ----

    [Fact]
    public async Task Closing_a_closed_ticket_is_a_conflict()
    {
        var id = await CreateTicketAsync();
        await _client.PostAsync($"/api/v1/tickets/{id}/close", null);

        var response = await _client.PostAsync($"/api/v1/tickets/{id}/close", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await Problem.ReadAsync(response);
        Assert.Equal(Problem.InvalidTransition, problem.Str("type"));
        Assert.Equal("Closed", problem.Str("currentStatus"));
    }

    [Fact]
    public async Task Assigning_a_closed_ticket_is_a_conflict()
    {
        var id = await CreateTicketAsync();
        await _client.PostAsync($"/api/v1/tickets/{id}/close", null);

        var response = await _client.PostAsJsonAsync($"/api/v1/tickets/{id}/assign", new { assigneeId = "dana" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Problem.InvalidTransition, (await Problem.ReadAsync(response)).Str("type"));
    }

    // ---- 500: our bug, not theirs ----

    [Fact]
    public async Task Unhandled_exception_is_a_problem_without_internals()
    {
        var response = await _client.GetAsync("/dev/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await Problem.ReadAsync(response);
        Assert.Equal(Problem.Unexpected, problem.Str("type"));

        var raw = problem.GetRawText();
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain("Boom", raw);
        Assert.DoesNotContain(".cs:line", raw);
    }

    // ---- Every error: a trace ID to correlate with logs ----

    [Theory]
    [InlineData("GET", "/api/v1/tickets/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v2/tickets")]
    [InlineData("GET", "/dev/boom")]
    public async Task Every_error_carries_a_trace_id(string method, string url)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        var problem = await Problem.ReadAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(problem.Str("traceId")), "Problem should include a traceId");
    }
}
