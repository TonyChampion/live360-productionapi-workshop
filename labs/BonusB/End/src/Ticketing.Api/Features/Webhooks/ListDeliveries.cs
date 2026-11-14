using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Webhooks;

public static class ListDeliveries
{
    public static RouteGroupBuilder MapListDeliveries(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/deliveries", Handle).WithName("ListDeliveries");
        return group;
    }

    /// <summary>What was sent and how it went: the first place a subscriber looks when "we never got it."</summary>
    public static Results<Ok<DeliveryResponse[]>, ProblemHttpResult> Handle(Guid id, WebhookStore store) =>
        store.Get(id) is null
            ? Problems.SubscriptionNotFound(id)
            : TypedResults.Ok(store.Deliveries(id).Select(d => d.ToResponse()).ToArray());
}
