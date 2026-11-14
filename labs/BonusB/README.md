# Bonus Lab B: Tell Other Systems

**Bonus B: Webhooks** | about 45 minutes

So far, everyone who wants to know about a ticket has to ask: poll `GET /tickets/{id}`, or poll Lab 8's job resource. A customer whose CRM should update when a ticket closes would poll every ticket, forever. **Webhooks** turn that around: the customer gives you a URL once, and you call it when something happens.

It sounds like "make an HTTP call," and it's really four problems: who may subscribe, how the receiver knows the call came from **you**, what happens when **their** server is down, and how to do all of it without slowing down the request that closed the ticket. You'll solve each with pieces you already have: Lab 8's event and queue, Lab 7's resilience, Lab 2's Problem Details.

**You're done when** agents can subscribe a URL and get a signing secret, closing a ticket (one or in bulk) delivers a signed webhook in the background, a failing receiver is retried with the same id and the outcome is recorded, and `dotnet test` is fully green.

This lab starts from **Lab 8's End**, and doesn't depend on Bonus A.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/BonusB/Start` and run this lab's tests:

```
dotnet test --filter "Lab=B"
```

**All 12 fail.** Every endpoint this lab adds is new.

The delivery tests don't use the network. They replace the last handler in the API's outbound `HttpClient` with a fake receiver that records what arrives and answers whatever the test says, so everything in front of it (including the resilience you'll add in Step 3) is real. They also check each signature **from the spec**, without calling your code, the way a customer's server would.

### What's new in your Start folder

- **`Microsoft.Extensions.Http.Resilience`**, pinned in `Directory.Packages.props` and referenced by the API. It's the HTTP flavor of Lab 7's resilience package.
- **`src/Ticketing.WebhookReceiver`**: a stand-in for the customer's server, on `http://localhost:5005`. It verifies signatures, ignores duplicates, and can be told to fail the next few deliveries. You'll run it in Step 4. Read its `Program.cs`: it's the other half of the contract you're about to write.
- **`http/B-webhooks.http`** covers every step.

---

## Step 1: Let agents subscribe (10 min)

A subscription is a URL, the events it wants, and a **secret** shared between you and the receiver. The secret is how the receiver will know a call is genuine, so it's shown **once**, in the response that creates the subscription, and never again (like an API key from any cloud console).

Create the folder `src/Ticketing.Api/Features/Webhooks`, and in it `WebhookContracts.cs`:

```csharp
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
```

Two response types, not one with a nullable secret: the list endpoint **can't** leak the secret, because its type doesn't have one.

`WebhookStore.cs` holds subscriptions and their delivery history:

```csharp
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
```

The secret format (`whsec_` plus random bytes in base64) comes from **[Standard Webhooks](https://www.standardwebhooks.com/)**, an open spec for signing and delivering webhooks. Following it means your customers can verify your webhooks with an off-the-shelf library in any language, instead of reading your docs and writing crypto code.

`ManageSubscriptions.cs` has the three endpoints for subscribing, listing, and unsubscribing:

```csharp
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
```

The validation attributes from Lab 2 check the **shape** (present, not too long). Whether a string is an absolute `https` URL, or an event type that exists, is **meaning**, so the handler checks it and answers with the same 400 Validation Problem, keyed by the same camelCase field names.

`ListDeliveries.cs`:

```csharp
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
```

Add a problem type to `Infrastructure/Problems.cs`. In `ProblemTypes`, after `JobNotFound`:

```csharp
    public const string SubscriptionNotFound = Base + "subscription-not-found"; // 404: no such webhook subscription
```

And in `Problems`, after `JobNotFound`:

```csharp
    public static ProblemHttpResult SubscriptionNotFound(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            type: ProblemTypes.SubscriptionNotFound,
            title: "Subscription not found",
            detail: $"No webhook subscription with id {id} exists.");
```

Create `WebhooksExtensions.cs`, for now with just the store:

```csharp
namespace Ticketing.Api.Features.Webhooks;

public static class WebhooksExtensions
{
    public static IServiceCollection AddWebhooks(this IServiceCollection services)
    {
        services.AddSingleton<WebhookStore>();
        return services;
    }
}
```

Map the endpoints in `Features/TicketingEndpoints.cs` (add `using Ticketing.Api.Features.Webhooks;`), after the escalations group. Only agents manage webhooks:

```csharp
        api.MapGroup("/webhooks")
            .WithTags("Webhooks")
            .RequireAuthorization(Policies.Agent)
            .MapWebhookSubscriptions()
            .MapListDeliveries();
```

And register in `Program.cs` (add `using Ticketing.Api.Features.Webhooks;`), after `AddTicketJobs()`:

```csharp
builder.Services.AddWebhooks();
```

### Checkpoint

**7 of 12 passing**: subscribing, the secret shown once, agents only, the four bad requests, and unsubscribing (which passes for now because nothing is delivered to anyone yet). Try the first few requests in `B-webhooks.http`.

---

## Step 2: Deliver from the event (15 min)

Lab 8's `CloseTicket` (and the bulk close worker) already announce `TicketClosed`. Webhooks are one more subscriber, and the close slice doesn't change at all.

But the handler runs **inside** the close request (in-process events run before `PublishAsync` returns). If it called the customer's server, a slow receiver would make closing a ticket slow, and a dead one would make it fail. So the handler only **writes down** what to send, and a background dispatcher sends it: Lab 8's accept-then-work pattern again.

First the signature. `WebhookSigner.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Ticketing.Api.Features.Webhooks;

/// <summary>
/// Standard Webhooks signing (standardwebhooks.com), so receivers can use an off-the-shelf verifier.
/// The signature covers the id and timestamp as well as the body: change any of them, and it no longer matches.
/// </summary>
public static class WebhookSigner
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    public static string Sign(string secret, string id, long timestamp, string body)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
        return "v1," + Convert.ToBase64String(signature);
    }
}
```

The receiver recomputes the HMAC with its copy of the secret. If it matches, the call came from someone who knows the secret, and nobody changed the id, the timestamp, or a single byte of the body on the way. The timestamp is signed so an attacker can't capture a genuine webhook and replay it next week: the receiver rejects anything older than a few minutes (look at step 1 in the receiver's `Program.cs`).

`WebhookQueue.cs`, the same channel shape as Lab 8's job queue:

```csharp
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
```

`NotifyWebhooksOnClose.cs`, the event handler:

```csharp
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
```

Two decisions are made here, once, at the moment of the event: the **delivery id** and the **body**. Every attempt to deliver this event will reuse both. That's what lets a receiver say "I've seen `msg_4f2a...` already" and ignore a repeat.

`WebhookDispatcher.cs`, the background service that sends:

```csharp
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
```

- **`Parallel.ForEachAsync`** reads the queue and delivers up to four at a time. Lab 8's worker did one job at a time, which is fine for your own store. Here, the far end is someone else's server: one that takes ten seconds to answer would hold up every other customer's webhooks behind it.
- **A failed delivery is recorded, not thrown.** The receiver being down isn't your bug, and an exception escaping `ExecuteAsync` would stop the dispatcher for everyone.
- **`IHttpClientFactory`** with a named client, so the handlers behind it (and the connections) are managed for you, and so Step 3 and the tests can configure that one client by name.

Update `WebhooksExtensions.cs`:

```csharp
using Ticketing.Api.Features.Events;

namespace Ticketing.Api.Features.Webhooks;

public static class WebhooksExtensions
{
    public static IServiceCollection AddWebhooks(this IServiceCollection services)
    {
        services.AddSingleton<WebhookStore>();
        services.AddSingleton<WebhookQueue>();

        // The webhooks slice subscribes itself to the event: nothing in Events/ or Tickets/ changes
        services.AddScoped<ITicketEventHandler<TicketClosed>, NotifyWebhooksOnClose>();

        services.AddHttpClient(WebhookDispatcher.ClientName);
        services.AddHostedService<WebhookDispatcher>();
        return services;
    }
}
```

### Checkpoint: two tests are still red

**10 of 12 passing.** Closing a ticket delivers a signed webhook, bulk close delivers one per ticket, and closing doesn't wait for a slow receiver.

The two failing tests are about failure. `A_failing_receiver_is_retried_with_the_same_webhook_id` sends one 503 and gives up. `A_receiver_that_keeps_failing_is_recorded_as_failed` expects four tries and gets one. A plain `HttpClient` makes one attempt, and the customer's server blinking for a second (a deploy, a restart) means a lost event.

---

## Step 3: Retry like you mean it (5 min)

Lab 7 built a resilience pipeline by hand for the store: total timeout, retry, circuit breaker, attempt timeout. For outbound HTTP, **`AddStandardResilienceHandler()`** gives you that same pipeline in one call, already tuned for HTTP: it retries 5xx, 408, 429, and connection failures, with exponential backoff and jitter, and honors `Retry-After`.

Replace `WebhooksExtensions.cs` with the finished version:

```csharp
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
```

- **`.Configure(...)`** sets the parts you care about from configuration. Everything else keeps the standard defaults.
- **Retrying a POST.** Lab 7's rule was "never retry a write that might have happened." The standard handler retries every method by default, and here that's right: every attempt carries the same `webhook-id`, so the receiver can drop repeats. The rule is really "only retry what's safe to repeat," and you made it safe in Step 2.
- **`.SelectPipelineByAuthority()`** gives each receiver (each host and port) its own pipeline, and so its own **circuit breaker**. With one shared breaker, one customer's dead server would open the circuit and stop webhooks to every other customer.

Add the settings to `appsettings.json`, before `"Sla"`:

```json
  "Webhooks": {
    "MaxRetries": 3,
    "RetryDelayMs": 2000,
    "AttemptTimeoutSeconds": 10
  },
```

The tests set `Webhooks:RetryDelayMs` to 10, so they don't wait seconds between attempts.

### Checkpoint

**All 12 passing.** Run the whole suite too (`dotnet test`).

---

## Step 4: Try it end to end (10 min)

Now the real thing: two processes, a real network call, and a signature checked by code that isn't yours.

1. Run the API, and subscribe with the first request in `B-webhooks.http`. Copy the `secret` from the response.
2. In a second terminal, start the receiver with that secret:

   ```
   dotnet run --project src/Ticketing.WebhookReceiver -- --Webhook:Secret whsec_...
   ```

3. Create a ticket and close it. The close comes back immediately, and the receiver's console shows `verified` with the body.
4. `POST http://localhost:5005/fail-next/2`, then close another ticket. The receiver logs two `failing on purpose (503)` lines and then `verified`, **all with the same `msg_` id**. The deliveries list in the API shows it `Succeeded`, a few seconds after it was created (the backoff).
5. `POST http://localhost:5005/fail-next/10`, and close one more. After four tries, the delivery is `Failed`, with `statusCode` 503.
6. Restart the receiver with a **wrong** secret (change one character) and close a ticket: the receiver logs `bad signature` and answers 401. A 401 isn't a transient failure, so the API doesn't retry it.

---

## Finished

Your `Program.cs` gained one line:

```csharp
builder.Services.AddTicketJobs();
builder.Services.AddWebhooks();
builder.Services.AddTicketingAuth();
```

The webhooks slice, all in `Features/Webhooks`:

| File | Job |
|---|---|
| `ManageSubscriptions.cs`, `ListDeliveries.cs` | The API: subscribe, list, unsubscribe, delivery history |
| `NotifyWebhooksOnClose.cs` | Hears `TicketClosed`, decides the id and body, queues the delivery |
| `WebhookDispatcher.cs` | Signs and sends, a few at a time, and records the outcome |
| `WebhooksExtensions.cs` | Wires it up: the event handler, the client, the resilience |

Nothing outside the folder changed except one line in `Program.cs`, one group in `TicketingEndpoints.cs`, and a problem type. That's the vertical slice from Lab 1, and the events from Lab 8, paying off.

---

## Talk it over

### A URL from a customer is an attack surface

Your server now makes requests to URLs that users type in. Someone subscribes `http://169.254.169.254/latest/meta-data/` (the cloud metadata service) or `http://localhost:8080/admin`, and your API fetches it from inside your network. That's **SSRF** (server-side request forgery). In production: require `https`, resolve the host and refuse private, loopback, and link-local addresses (check again at send time, since DNS can change), don't follow redirects, and send from a network segment that can't reach anything internal. The lab allows `http://localhost` only so the receiver works on your machine.

### In memory means "lost on restart"

A webhook queued in a `Channel` is gone if the process restarts before it's sent, and the standard handler's retries span seconds, not hours. Real providers retry for a day or more (Stripe: up to three days), which needs a durable queue and a stored "next attempt" time, and a **new timestamp and signature for each attempt**, since receivers reject old timestamps. And the gap from Lab 8 is still here: the ticket can close and the process die before the event is queued. The outbox pattern closes it.

### Rotating a secret

The `webhook-signature` header can carry several signatures, space-separated. To rotate, sign with both the old and the new secret for a while, so receivers can switch whenever they're ready, then drop the old one. The receiver in this lab already accepts any matching signature in the list.

### Thin or fat events

This payload is **thin**: the ticket id and who closed it. The receiver calls `GET /tickets/{id}` for the rest, with its own credentials, so it always sees current data and nothing leaks to a URL that shouldn't have it. A **fat** event (the whole ticket) saves that call, but goes stale and makes every field part of the webhook contract. Thin is the safer default.

### Ordering, duplicates, and dead endpoints

Webhooks arrive **at least once** and **in no guaranteed order** (four in parallel, plus retries). Tell receivers so: dedupe on `webhook-id`, and use the payload's `timestamp` rather than arrival order. And an endpoint that has failed every delivery for days should be disabled, with an email to its owner, rather than retried forever.
