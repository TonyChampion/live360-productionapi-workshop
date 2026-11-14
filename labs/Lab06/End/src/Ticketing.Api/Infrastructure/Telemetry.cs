using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ticketing.Api.Infrastructure;

public static class Telemetry
{
    public const string SourceName = "Ticketing.Api";

    /// <summary>
    /// Where our own spans come from. Plain .NET (System.Diagnostics): instrumenting code needs
    /// no OpenTelemetry package. OpenTelemetry is only how the spans get exported.
    /// </summary>
    public static readonly ActivitySource Source = new(SourceName);

    public static IServiceCollection AddTicketingTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("ticketing-api"))
            .WithTracing(tracing => tracing
                .AddSource(SourceName)
                .AddAspNetCoreInstrumentation(options =>
                    // Probes and lab plumbing would drown out the real traffic
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health") &&
                        !context.Request.Path.StartsWithSegments("/dev")));

        // Export over OTLP only when a collector is configured, e.g. the Aspire dashboard:
        //   OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        return services;
    }
}
