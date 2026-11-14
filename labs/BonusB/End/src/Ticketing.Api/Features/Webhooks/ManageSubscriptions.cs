using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Webhooks;

public static class ManageSubscriptions
{
    public static RouteGroupBuilder MapWebhookSubscriptions(this RouteGroupBuilder group)
    {
        group.MapPost("/", Subscribe).WithName("Subscribe");
        group.MapGet("/", List).WithName("ListSubscriptions");
        group.MapDelete("/{id:guid}", Unsubscribe).WithName("Unsubscribe");
        return group;
    }

    public static Results<Created<SubscriptionCreatedResponse>, ValidationProblem> Subscribe(
        SubscribeRequest request, WebhookStore store, ClaimsPrincipal user, HttpRequest http)
    {
        // The attributes check shape; these check meaning. Same 400 either way.
        var errors = new Dictionary<string, string[]>();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            errors["url"] = ["The url must be an absolute http or https URL."];
        var unknown = request.Events.Except(WebhookEvents.All).ToArray();
        if (unknown.Length > 0)
            errors["events"] = [$"Unknown event types: {string.Join(", ", unknown)}. Known: {string.Join(", ", WebhookEvents.All)}."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var subscription = store.Add(new WebhookSubscription
        {
            Url = request.Url,
            Events = request.Events.Distinct().ToArray(),
            Secret = WebhookStore.NewSecret(),
            CreatedBy = user.UserId()
        });

        return TypedResults.Created(http.LocationFor(subscription.Id), new SubscriptionCreatedResponse(
            subscription.Id, subscription.Url, subscription.Events, subscription.CreatedAt, subscription.Secret));
    }

    public static Ok<SubscriptionResponse[]> List(WebhookStore store) =>
        TypedResults.Ok(store.List().Select(s => s.ToResponse()).ToArray());

    public static Results<NoContent, ProblemHttpResult> Unsubscribe(Guid id, WebhookStore store) =>
        store.Remove(id) ? TypedResults.NoContent() : Problems.SubscriptionNotFound(id);
}
