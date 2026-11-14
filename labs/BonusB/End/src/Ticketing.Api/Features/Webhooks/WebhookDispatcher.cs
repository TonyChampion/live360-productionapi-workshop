using System.Text;

namespace Ticketing.Api.Features.Webhooks;

/// <summary>
/// Sends queued webhooks. A few at a time, so one slow receiver doesn't hold up everyone else's.
/// </summary>
public class WebhookDispatcher(
    WebhookQueue queue, WebhookStore store, IHttpClientFactory httpClients, ILogger<WebhookDispatcher> log)
    : BackgroundService
{
    public const string ClientName = "webhooks";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Parallel.ForEachAsync(queue.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken },
            DeliverAsync);

    private async ValueTask DeliverAsync(PendingWebhook webhook, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
        {
            Content = new StringContent(webhook.Body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WebhookSigner.IdHeader, webhook.DeliveryId);
        request.Headers.Add(WebhookSigner.TimestampHeader, timestamp.ToString());
        request.Headers.Add(WebhookSigner.SignatureHeader,
            WebhookSigner.Sign(webhook.Secret, webhook.DeliveryId, timestamp, webhook.Body));

        int? statusCode = null;
        string? error = null;
        try
        {
            using var response = await httpClients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                error = $"The receiver answered {statusCode}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Unreachable, timed out, or the circuit is open: the receiver's problem, recorded, not thrown
            error = ex.Message;
        }

        var status = error is null ? DeliveryStatus.Succeeded : DeliveryStatus.Failed;
        if (status == DeliveryStatus.Failed)
            log.LogWarning("Webhook {DeliveryId} to {Url} failed: {Error}", webhook.DeliveryId, webhook.Url, error);

        store.Update(webhook.DeliveryId, d => d with
        {
            Status = status, StatusCode = statusCode, Error = error, CompletedAt = DateTimeOffset.UtcNow
        });
    }
}
