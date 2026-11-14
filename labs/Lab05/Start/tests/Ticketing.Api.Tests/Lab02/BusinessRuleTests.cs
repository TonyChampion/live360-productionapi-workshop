using System.Net;
using System.Net.Http.Json;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab02;

/// <summary>
/// Rules attributes can't express. An urgent ticket needs enough description for someone to act on it:
/// a rule about one field that depends on another, enforced with FluentValidation.
/// </summary>
[Trait(Lab.Trait, "2")]
public class BusinessRuleTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Urgent_ticket_without_a_real_description_is_a_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Server down", description = "Help!", priority = "Urgent", requesterId = "bob" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await Problem.ReadAsync(response);
        Assert.Equal(Problem.Validation, problem.Str("type"));
        Assert.NotEmpty(problem.ErrorsFor("description"));
        Assert.NotNull(problem.Str("traceId"));
    }

    [Fact]
    public async Task Urgent_ticket_with_a_real_description_is_created()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Server down", description = "The build server stopped responding at 9:05.", priority = "Urgent", requesterId = "bob" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task The_rule_only_applies_to_urgent_tickets()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Minor typo", description = "", priority = "Low", requesterId = "bob" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
