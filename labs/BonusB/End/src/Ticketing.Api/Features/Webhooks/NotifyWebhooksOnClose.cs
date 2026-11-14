using System.Text.Json;
using Ticketing.Api.Features.Events;

namespace Ticketing.Api.Features.Webhooks;

/// <summary>
/// Lab 8's TicketClosed event, one more subscriber. The close slice doesn't change, and doesn't wait:
/// this handler only writes down what to send. Sending is the dispatcher's job.
/// </summary>
public class NotifyWebhooksOnClose(WebhookStore store, WebhookQueue queue) : ITicketEventHandler<TicketClosed>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(TicketClosed ticketEvent, CancellationToken cancellationToken)
    {
        foreach (var subscription in store.SubscribedTo(WebhookEvents.TicketClosed))
        {
            // One id per event per subscriber, kept across every retry, so receivers can drop duplicates
            var deliveryId = $"msg_{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;

            // The body is fixed now, so every attempt sends (and signs) exactly the same bytes
            var body = JsonSerializer.Serialize(new
            {
                type = WebhookEvents.TicketClosed,
                timestamp = now,
                data = new { ticketId = ticketEvent.TicketId, closedBy = ticketEvent.ClosedBy }
            }, Json);

            store.Record(new WebhookDelivery(deliveryId, subscription.Id, WebhookEvents.TicketClosed,
                ticketEvent.TicketId, DeliveryStatus.Pending, null, null, now, null));

            await queue.EnqueueAsync(new PendingWebhook(deliveryId, subscription.Id, subscription.Url,
                subscription.Secret, WebhookEvents.TicketClosed, body), cancellationToken);
        }
    }
}
