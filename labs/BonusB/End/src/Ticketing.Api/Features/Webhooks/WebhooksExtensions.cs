using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Ticketing.Api.Features.Events;

namespace Ticketing.Api.Features.Webhooks;

/// <summary>Bound from Webhooks.</summary>
public class WebhookOptions
{
    public int MaxRetries { get; set; } = 3;
    /// <summary>The first retry's wait. Each later one roughly doubles, with jitter.</summary>
    public int RetryDelayMs { get; set; } = 2000;
    public int AttemptTimeoutSeconds { get; set; } = 10;
}

public static class WebhooksExtensions
{
    public static IServiceCollection AddWebhooks(this IServiceCollection services)
    {
        services.AddOptions<WebhookOptions>().BindConfiguration("Webhooks");
        services.AddSingleton<WebhookStore>();
        services.AddSingleton<WebhookQueue>();

        // The webhooks slice subscribes itself to the event: nothing in Events/ or Tickets/ changes
        services.AddScoped<ITicketEventHandler<TicketClosed>, NotifyWebhooksOnClose>();

        // Lab 7 built a pipeline by hand. For outbound HTTP, one call gives you the standard one:
        // total timeout -> retry -> circuit breaker -> attempt timeout, tuned for HTTP's transient failures
        services.AddHttpClient(WebhookDispatcher.ClientName)
            .AddStandardResilienceHandler()
            .Configure((HttpStandardResilienceOptions options, IServiceProvider sp) =>
            {
                var webhooks = sp.GetRequiredService<IOptions<WebhookOptions>>().Value;
                options.Retry.MaxRetryAttempts = webhooks.MaxRetries;
                options.Retry.Delay = TimeSpan.FromMilliseconds(webhooks.RetryDelayMs);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(webhooks.AttemptTimeoutSeconds);
                // Retrying a POST is safe here only because every attempt carries the same webhook-id
            })
            // One pipeline (and one circuit breaker) per receiver: a dead endpoint can't trip everyone's
            .SelectPipelineByAuthority();

        services.AddHostedService<WebhookDispatcher>();
        return services;
    }
}
