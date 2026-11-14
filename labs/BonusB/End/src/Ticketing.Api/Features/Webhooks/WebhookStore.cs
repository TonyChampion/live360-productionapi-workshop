using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Ticketing.Api.Features.Webhooks;

public class WebhookSubscription
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; init; }
    public required string[] Events { get; init; }
    public required string Secret { get; init; }
    public string CreatedBy { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum DeliveryStatus { Pending, Succeeded, Failed }

public record WebhookDelivery(
    string Id, Guid SubscriptionId, string EventType, Guid TicketId, DeliveryStatus Status,
    int? StatusCode, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>
/// Subscriptions and their delivery history. In memory for the lab; in production a table each,
/// with the secrets encrypted at rest (they're as sensitive as passwords).
/// </summary>
public class WebhookStore
{
    private readonly ConcurrentDictionary<Guid, WebhookSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, WebhookDelivery> _deliveries = new();

    /// <summary>"whsec_" plus 24 random bytes, base64: the Standard Webhooks secret format.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    public WebhookSubscription Add(WebhookSubscription subscription) => _subscriptions[subscription.Id] = subscription;

    public bool Remove(Guid id) => _subscriptions.TryRemove(id, out _);

    public WebhookSubscription? Get(Guid id) => _subscriptions.GetValueOrDefault(id);

    public IReadOnlyList<WebhookSubscription> List() => _subscriptions.Values.OrderBy(s => s.CreatedAt).ToList();

    public IReadOnlyList<WebhookSubscription> SubscribedTo(string eventType) =>
        _subscriptions.Values.Where(s => s.Events.Contains(eventType)).ToList();

    public void Record(WebhookDelivery delivery) => _deliveries[delivery.Id] = delivery;

    public void Update(string deliveryId, Func<WebhookDelivery, WebhookDelivery> change)
    {
        if (_deliveries.TryGetValue(deliveryId, out var delivery))
            _deliveries[deliveryId] = change(delivery);
    }

    public IReadOnlyList<WebhookDelivery> Deliveries(Guid subscriptionId) =>
        _deliveries.Values.Where(d => d.SubscriptionId == subscriptionId).OrderBy(d => d.CreatedAt).ToList();
}
