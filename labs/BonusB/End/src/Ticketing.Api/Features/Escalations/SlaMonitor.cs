using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Escalations;

/// <summary>Bound from the "Sla" section.</summary>
public class SlaOptions
{
    /// <summary>How long an urgent or high ticket may wait for someone to pick it up.</summary>
    public int FirstResponseMinutes { get; set; } = 60;
    public int ScanIntervalMs { get; set; } = 30_000;
}

public record Escalation(Guid TicketId, string Title, TicketPriority Priority, DateTimeOffset EscalatedAt);

public class EscalationLog
{
    private readonly ConcurrentDictionary<Guid, Escalation> _escalations = new();

    /// <summary>False if the ticket was already escalated: each ticket is escalated once.</summary>
    public bool TryAdd(Escalation escalation) => _escalations.TryAdd(escalation.TicketId, escalation);

    public IReadOnlyList<Escalation> All() => _escalations.Values.OrderBy(e => e.EscalatedAt).ToList();
}

/// <summary>
/// Work on a schedule instead of on a request: every interval, find urgent and high tickets
/// that are still open and unassigned past their first-response window, and escalate them.
/// </summary>
public class SlaMonitor(ITicketStore store, EscalationLog escalations, IOptions<SlaOptions> options,
    ILogger<SlaMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.ScanIntervalMs));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // An unhandled exception in a BackgroundService stops the whole app (since .NET 6).
                // A failed scan isn't worth that: log it and try again next interval.
                logger.LogWarning(ex, "SLA scan failed; will try again in {IntervalMs} ms", options.Value.ScanIntervalMs);
            }
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-options.Value.FirstResponseMinutes);
        var tickets = await store.ListAsync(cancellationToken);

        var overdue = tickets.Where(t =>
            t.Status == TicketStatus.Open &&
            t.AssigneeId is null &&
            t.Priority >= TicketPriority.High &&
            t.CreatedAt < cutoff);

        foreach (var ticket in overdue)
            if (escalations.TryAdd(new Escalation(ticket.Id, ticket.Title, ticket.Priority, DateTimeOffset.UtcNow)))
                logger.LogWarning("Ticket {TicketId} ({Priority}) escalated: no one has picked it up", ticket.Id, ticket.Priority);
    }
}

public static class ListEscalations
{
    public static RouteGroupBuilder MapListEscalations(this RouteGroupBuilder group)
    {
        group.MapGet("/", (EscalationLog log) => TypedResults.Ok(log.All())).WithName("ListEscalations");
        return group;
    }
}
