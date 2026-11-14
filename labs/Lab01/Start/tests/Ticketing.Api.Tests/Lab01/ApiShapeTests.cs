using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab01;

/// <summary>
/// What Lab 1 adds on top of the migration: contracts instead of domain models,
/// versioning that reports itself, an OpenAPI document, and no controllers left.
/// </summary>
[Trait(Lab.Trait, "1")]
public class ApiShapeTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Ticket_response_is_a_contract_not_the_domain_model()
    {
        var json = await _client.GetStringAsync($"/api/v1/tickets/{SeedData.FirstTicketId}");
        var ticket = JsonDocument.Parse(json).RootElement;

        // Comments are their own resource; the ticket only says how many there are
        Assert.False(ticket.TryGetProperty("comments", out _), "Ticket response should not embed comments");
        Assert.True(ticket.TryGetProperty("commentCount", out var count), "Ticket response should include commentCount");
        Assert.True(count.GetInt32() >= 1);
    }

    [Fact]
    public async Task Create_ignores_fields_the_caller_does_not_own()
    {
        // Overposting: a requester tries to assign and close their own ticket on create
        var response = await _client.PostAsJsonAsync("/api/v1/tickets", new
        {
            title = "Please fix",
            description = "Sneaky fields below.",
            priority = "Low",
            requesterId = "mallory",
            assigneeId = "mallory",
            status = "Closed"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json);
        Assert.Null(created!.AssigneeId);
        Assert.Equal("Open", created.Status);
    }

    [Fact]
    public async Task Responses_report_supported_api_versions()
    {
        var response = await _client.GetAsync("/api/v1/tickets");

        Assert.True(response.Headers.TryGetValues("api-supported-versions", out var versions),
            "Expected an api-supported-versions header");
        Assert.Contains("1.0", string.Join(",", versions!));
    }

    [Fact]
    public async Task Unsupported_api_version_is_rejected()
    {
        // With URL segment versioning, a version that doesn't exist is a resource that doesn't exist
        var response = await _client.GetAsync("/api/v2/tickets");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_lists_every_ticket_endpoint()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");
        var paths = JsonDocument.Parse(json).RootElement.GetProperty("paths");
        var names = paths.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Contains("/api/v1/tickets", names);
        Assert.Contains("/api/v1/tickets/{id}", names);
        Assert.Contains("/api/v1/tickets/{ticketId}/comments", names);
    }

    [Fact]
    public void No_controllers_remain()
    {
        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(controllers);
    }
}
