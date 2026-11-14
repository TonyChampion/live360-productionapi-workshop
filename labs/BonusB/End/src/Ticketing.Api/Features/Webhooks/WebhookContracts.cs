using System.ComponentModel.DataAnnotations;

namespace Ticketing.Api.Features.Webhooks;

public static class WebhookEvents
{
    public const string TicketClosed = "ticket.closed";

    /// <summary>What can be subscribed to. Each new event type is a new promise to every subscriber.</summary>
    public static readonly string[] All = [TicketClosed];
}

public record SubscribeRequest(
    [Required, StringLength(2000)] string Url,
    [Required, MinLength(1)] string[] Events);

// The secret appears in exactly one response: the one that creates the subscription
public record SubscriptionCreatedResponse(Guid Id, string Url, string[] Events, DateTimeOffset CreatedAt, string Secret);

public record SubscriptionResponse(Guid Id, string Url, string[] Events, DateTimeOffset CreatedAt);

public record DeliveryResponse(
    string Id, string EventType, Guid TicketId, DeliveryStatus Status, int? StatusCode, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public static class WebhookMappings
{
    public static SubscriptionResponse ToResponse(this WebhookSubscription s) => new(s.Id, s.Url, s.Events, s.CreatedAt);

    public static DeliveryResponse ToResponse(this WebhookDelivery d) => new(
        d.Id, d.EventType, d.TicketId, d.Status, d.StatusCode, d.Error, d.CreatedAt, d.CompletedAt);
}
