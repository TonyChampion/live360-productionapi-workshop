using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Ticketing.Api.Chaos;
using Ticketing.Api.Data;
using Ticketing.Api.Tests.Infrastructure;

namespace Ticketing.Api.Tests.Lab08;

public record JobItemDto(Guid TicketId, string Outcome);
public record JobDto(Guid Id, string Kind, string Status, int Total, int Completed, int Attempts,
    string? LastError, List<JobItemDto> Results, string? TraceId);
public record EscalationDto(Guid TicketId, string Title, string Priority, DateTimeOffset EscalatedAt);

/// <summary>
/// When a request shouldn't do the work: accept it, hand it to a worker, and let the caller follow along.
/// Tests that inject failures or change timings run in their own copy of the app.
/// </summary>
[Trait(Lab.Trait, "8")]
public class BackgroundTests(TicketingApiFactory factory) : IClassFixture<TicketingApiFactory>
{
    private const string Problems = "https://ticketing.example/problems/";
    private readonly HttpClient _client = factory.CreateClient();

    private WebApplicationFactory<Program> App(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jobs:RetryDelayMs", "50");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });

    private static async Task<Guid> CreateTicket(HttpClient client, string title = "Bulk test")
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets",
            new { title, description = "For a test.", priority = "Low", requesterId = "carol" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json))!.Id;
    }

    private static Task<HttpResponseMessage> BulkClose(HttpClient client, params Guid[] ids) =>
        client.PostAsJsonAsync("/api/v1/tickets/bulk-close", new { ticketIds = ids });

    private static async Task<JobDto> WaitForJob(HttpClient client, Uri location, Func<JobDto, bool> done, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        JobDto? job = null;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            job = await client.GetFromJsonAsync<JobDto>(location, TicketingApiFactory.Json);
            if (done(job!)) return job!;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job never finished; last seen: {job}");
    }

    private static bool Finished(JobDto job) => job.Status is "Succeeded" or "DeadLettered";

    private static async Task<string> ProblemType(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("type").GetString()!;
    }

    // ---- In-process events ----

    [Fact]
    public async Task Closing_a_ticket_adds_a_closing_comment()
    {
        var id = await CreateTicket(_client);

        await _client.PostAsync($"/api/v1/tickets/{id}/close", null);

        var comments = await _client.GetFromJsonAsync<List<CommentDto>>($"/api/v1/tickets/{id}/comments", TicketingApiFactory.Json);
        Assert.Contains(comments!, c => c.AuthorId == "system" && c.Body.Contains("closed by dana"));
    }

    // ---- 202 and the job resource ----

    [Fact]
    public async Task Bulk_close_is_accepted_with_a_status_location()
    {
        var id = await CreateTicket(_client);

        var response = await BulkClose(_client, id);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Matches(@"/api/v1/jobs/[0-9a-f-]{36}$", response.Headers.Location!.ToString());
        var job = await response.Content.ReadFromJsonAsync<JobDto>(TicketingApiFactory.Json);
        Assert.Equal("bulk-close", job!.Kind);
        Assert.Equal(1, job.Total);
    }

    [Fact]
    public async Task A_bulk_close_job_closes_every_ticket_and_raises_the_event_for_each()
    {
        var ids = new[] { await CreateTicket(_client), await CreateTicket(_client), await CreateTicket(_client) };

        var accepted = await BulkClose(_client, ids);
        var job = await WaitForJob(_client, accepted.Headers.Location!, Finished);

        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(3, job.Completed);
        foreach (var id in ids)
        {
            var ticket = await _client.GetFromJsonAsync<TicketDto>($"/api/v1/tickets/{id}", TicketingApiFactory.Json);
            Assert.Equal("Closed", ticket!.Status);
            var comments = await _client.GetFromJsonAsync<List<CommentDto>>($"/api/v1/tickets/{id}/comments", TicketingApiFactory.Json);
            Assert.Contains(comments!, c => c.AuthorId == "system");
        }
    }

    [Fact]
    public async Task Already_closed_and_unknown_tickets_are_reported_not_failed()
    {
        var closed = await CreateTicket(_client);
        await _client.PostAsync($"/api/v1/tickets/{closed}/close", null);
        var open = await CreateTicket(_client);
        var missing = Guid.NewGuid();

        var accepted = await BulkClose(_client, open, closed, missing);
        var job = await WaitForJob(_client, accepted.Headers.Location!, Finished);

        Assert.Equal("Succeeded", job.Status);
        Assert.Contains(job.Results, r => r.TicketId == open && r.Outcome == "closed");
        Assert.Contains(job.Results, r => r.TicketId == closed && r.Outcome == "already-closed");
        Assert.Contains(job.Results, r => r.TicketId == missing && r.Outcome == "not-found");
    }

    [Fact]
    public async Task The_request_returns_before_the_work_is_done()
    {
        using var app = App();
        var client = app.CreateClient();
        var ids = new[] { await CreateTicket(client), await CreateTicket(client), await CreateTicket(client) };
        await client.PutAsJsonAsync("/dev/chaos", new ChaosState(LatencyMs: 300, 0, true), TicketingApiFactory.Json);

        var stopwatch = Stopwatch.StartNew();
        var accepted = await BulkClose(client, ids);
        var acceptedAfter = stopwatch.ElapsedMilliseconds;
        var job = await WaitForJob(client, accepted.Headers.Location!, Finished);
        var doneAfter = stopwatch.ElapsedMilliseconds;

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(acceptedAfter < 250, $"202 took {acceptedAfter} ms; it shouldn't touch the store");
        Assert.Equal("Succeeded", job.Status);
        Assert.True(doneAfter > 1000, $"The job finished in {doneAfter} ms; with 300 ms latency it can't have done the work");
    }

    [Fact]
    public async Task Bulk_close_is_agents_only()
    {
        var response = await BulkClose(factory.CreateClientAs("alice", "requester"), SeedData.FirstTicketId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Bulk_close_validates_how_many_tickets(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

        var response = await BulkClose(_client, ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Problems + "validation", await ProblemType(response));
    }

    [Fact]
    public async Task An_unknown_job_is_a_job_not_found_problem()
    {
        var response = await _client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(Problems + "job-not-found", await ProblemType(response));
    }

    [Fact]
    public async Task The_job_runs_in_the_same_trace_as_the_request_that_queued_it()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Ticketing.Api",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        var accepted = await BulkClose(_client, await CreateTicket(_client));
        var job = await WaitForJob(_client, accepted.Headers.Location!, Finished);
        await Task.Delay(100);

        var traceId = job.TraceId!.Split('-')[1];
        Assert.Contains(spans, s => s.DisplayName == "job bulk-close" && s.TraceId.ToString() == traceId);
    }

    // ---- Retries and dead letters ----

    [Fact]
    public async Task A_job_that_fails_is_retried_and_finishes()
    {
        using var app = App();
        var client = app.CreateClient();
        var id = await CreateTicket(client);

        // 4 failures: more than the store's own retries absorb, so the first job attempt fails
        await client.PutAsJsonAsync("/dev/chaos", new ChaosState(0, 0, true, FailNextCalls: 4), TicketingApiFactory.Json);
        var accepted = await BulkClose(client, id);
        var job = await WaitForJob(client, accepted.Headers.Location!, Finished);

        Assert.Equal("Succeeded", job.Status);
        Assert.True(job.Attempts >= 2, $"Expected a retry; attempts = {job.Attempts}");
    }

    [Fact]
    public async Task A_retried_job_resumes_instead_of_closing_tickets_twice()
    {
        using var app = App(("Jobs:RetryDelayMs", "300"));
        var client = app.CreateClient();
        var first = await CreateTicket(client);
        var second = await CreateTicket(client);

        // Slow the store so the job is easy to interrupt partway through
        await client.PutAsJsonAsync("/dev/chaos", new ChaosState(LatencyMs: 200, 0, true), TicketingApiFactory.Json);
        var accepted = await BulkClose(client, first, second);
        var location = accepted.Headers.Location!;

        // As soon as the first ticket is done, take the store down: the attempt fails mid-job
        await WaitForJob(client, location, j => j.Completed >= 1);
        await client.PutAsJsonAsync("/dev/chaos", new ChaosState(0, 0, Available: false), TicketingApiFactory.Json);
        await WaitForJob(client, location, j => j.LastError is not null);

        // Bring it back before the retry: the next attempt picks up where the last one stopped
        await client.PostAsync("/dev/chaos/reset", null);
        var job = await WaitForJob(client, location, Finished);

        Assert.Equal("Succeeded", job.Status);
        Assert.True(job.Attempts >= 2, $"Expected a retry; attempts = {job.Attempts}");
        Assert.Equal(2, job.Results.Count);
        var comments = await client.GetFromJsonAsync<List<CommentDto>>($"/api/v1/tickets/{first}/comments", TicketingApiFactory.Json);
        Assert.Single(comments!, c => c.AuthorId == "system");   // announced once, not once per attempt
    }

    [Fact]
    public async Task A_job_that_keeps_failing_is_dead_lettered()
    {
        using var app = App(("Jobs:MaxAttempts", "3"));
        var client = app.CreateClient();
        var id = await CreateTicket(client);
        await client.PutAsJsonAsync("/dev/chaos", new ChaosState(0, 0, Available: false), TicketingApiFactory.Json);

        var accepted = await BulkClose(client, id);
        var job = await WaitForJob(client, accepted.Headers.Location!, Finished);

        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal(3, job.Attempts);
        Assert.False(string.IsNullOrWhiteSpace(job.LastError));
        Assert.DoesNotContain("Exception", job.LastError);
        var deadLetters = await client.GetFromJsonAsync<List<JobDto>>("/api/v1/jobs/dead-letters", TicketingApiFactory.Json);
        Assert.Contains(deadLetters!, j => j.Id == job.Id);
    }

    // ---- A timer: SLA escalations ----

    [Fact]
    public async Task Urgent_tickets_nobody_picked_up_are_escalated_by_a_background_timer()
    {
        using var app = App(("Sla:ScanIntervalMs", "100"));
        var client = app.CreateClient();
        var fresh = await client.PostAsJsonAsync("/api/v1/tickets",
            new { title = "Just arrived", description = "Nobody has picked this up yet.", priority = "Urgent", requesterId = "carol" });
        var freshId = (await fresh.Content.ReadFromJsonAsync<TicketDto>(TicketingApiFactory.Json))!.Id;

        List<EscalationDto>? escalations = null;
        for (var i = 0; i < 40 && !(escalations?.Count > 0); i++)
        {
            await Task.Delay(50);
            escalations = await client.GetFromJsonAsync<List<EscalationDto>>("/api/v1/escalations", TicketingApiFactory.Json);
        }

        // The seeded ticket is High, open, unassigned, and two weeks old
        Assert.Contains(escalations!, e => e.TicketId == SeedData.FirstTicketId);
        Assert.DoesNotContain(escalations!, e => e.TicketId == freshId);   // inside its first-response window
        var tickets = await client.GetFromJsonAsync<List<TicketDto>>("/api/v1/tickets?pageSize=100", TicketingApiFactory.Json);
        foreach (var e in escalations!)
        {
            var t = tickets!.Single(x => x.Id == e.TicketId);
            Assert.Equal("Open", t.Status);
            Assert.Null(t.AssigneeId);
            Assert.Contains(t.Priority, new[] { "High", "Urgent" });
        }
    }
}
