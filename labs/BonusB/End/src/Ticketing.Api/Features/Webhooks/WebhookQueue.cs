using System.Threading.Channels;

namespace Ticketing.Api.Features.Webhooks;

/// <summary>One delivery to make: everything the dispatcher needs, decided at the moment of the event.</summary>
public record PendingWebhook(string DeliveryId, Guid SubscriptionId, string Url, string Secret, string EventType, string Body);

/// <summary>Between "a ticket closed" and "tell the subscribers". Same shape as Lab 8's job queue.</summary>
public class WebhookQueue
{
    private readonly Channel<PendingWebhook> _channel = Channel.CreateBounded<PendingWebhook>(
        new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(PendingWebhook webhook, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(webhook, cancellationToken);

    public IAsyncEnumerable<PendingWebhook> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
