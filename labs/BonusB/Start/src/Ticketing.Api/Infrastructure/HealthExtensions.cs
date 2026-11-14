using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Ticketing.Api.Data;

namespace Ticketing.Api.Infrastructure;

/// <summary>Bound from HealthChecks:Store.</summary>
public class StoreHealthOptions
{
    public int DegradedMs { get; set; } = 500;
    public int TimeoutMs { get; set; } = 2000;
}

/// <summary>
/// Is the ticket store doing its job? Not just "can I reach it": slow counts as degraded, and
/// no answer within the timeout counts as unhealthy.
/// </summary>
public class StoreHealthCheck(
    ITicketStore store, IOptions<StoreHealthOptions> options, CircuitBreakerStateProvider circuit) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The breaker already knows the store is failing. Don't add to the load by probing it;
        // report Degraded: this instance still serves cached reads and fails the rest fast.
        if (circuit.CircuitState is CircuitState.Open or CircuitState.Isolated)
            return HealthCheckResult.Degraded("Circuit open: the store has been failing, so calls are paused while it recovers.");

        var limits = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.TimeoutMs);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await store.ListAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"No response within {limits.TimeoutMs} ms.");
        }
        catch (BrokenCircuitException)
        {
            return HealthCheckResult.Degraded("Circuit open: the store has been failing, so calls are paused while it recovers.");
        }
        catch (Exception)
        {
            // Deliberately not passing the exception: health output is public
            return HealthCheckResult.Unhealthy("The ticket store is unavailable.");
        }

        var elapsed = stopwatch.ElapsedMilliseconds;
        var data = new Dictionary<string, object> { ["responseTimeMs"] = elapsed };
        return elapsed > limits.DegradedMs
            ? HealthCheckResult.Degraded($"Responding slowly ({elapsed} ms).", data: data)
            : HealthCheckResult.Healthy("Responding normally.", data);
    }
}

public static class HealthExtensions
{
    private const string Ready = "ready";

    public static IServiceCollection AddTicketingHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StoreHealthOptions>(configuration.GetSection("HealthChecks:Store"));
        services.AddHealthChecks()
            .AddCheck<StoreHealthCheck>("ticket-store", tags: [Ready]);
        return services;
    }

    public static WebApplication MapTicketingHealth(this WebApplication app)
    {
        // Everything, for people and dashboards
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteJson });

        // Liveness: is the process alive? No dependencies, or a store outage restarts every instance
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteJson });

        // Readiness: should this instance get traffic right now?
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(Ready),
            ResponseWriter = WriteJson
        });

        return app;
    }

    /// <summary>
    /// A JSON report. The built-in writer only sends the word "Healthy". This writer sends each
    /// check's status, description, timing, and data, and never an exception.
    /// </summary>
    private static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                data = entry.Value.Data
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
