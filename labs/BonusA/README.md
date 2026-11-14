# Bonus Lab A: Safe to Retry

**Bonus A: Idempotency** | about 45 minutes

Lab 7 left a hole on purpose: a write that timed out **may have happened**, so the API never retries one. The caller is stuck in the same spot. Their `POST /tickets` timed out, or the Wi-Fi dropped before the 201 arrived. Did the ticket get created? If they retry and it did, there are two tickets. If they don't and it didn't, there are none.

An **`Idempotency-Key`** header (an [IETF draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/), and what Stripe, PayPal, and Adyen have done for years) closes the hole. The caller picks a key per operation and sends it on every retry of that operation. The API promises: **same key, same outcome, at most one ticket.** You'll build it in two layers:

1. **The key in the data.** The ticket id is derived from the key, so a retry finds the ticket the first call made. This holds across restarts and across instances.
2. **A response cache in front.** A retry gets back exactly the first call's response, without running the handler. A reused key with a different body is rejected, and so is a second call while the first is still running.

**You're done when** a create sent twice with the same key makes one ticket and replays the first response, misuse of a key gets a clear Problem Details answer, a failed create doesn't burn the key, and `dotnet test` is fully green.

This lab starts from **Lab 8's End**, and doesn't depend on Bonus B.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/BonusA/Start` and run this lab's tests:

```
dotnet test --filter "Lab=A"
```

**7 of 11 fail.** The 4 that pass are guards: they describe behavior that already works and must keep working (no key behaves exactly as before, a failed create can be retried, and so on).

### What's new in your Start folder

- **`http/A-idempotency.http`** covers every step. Get tokens for dana and erin (both agents) the way you did in Lab 3 (from `src/Ticketing.Api`):

  ```
  dotnet user-jwts create --name dana --role agent
  dotnet user-jwts create --name erin --role agent
  ```

---

## Step 1: Put the key in the data (10 min)

Start with the layer that can't forget. If the same caller and key always produce the same ticket id, then a retry can look the ticket up, and find it if the first call got that far. No cache needed, nothing to expire, and it works no matter which instance the retry lands on.

Create `src/Ticketing.Api/Infrastructure/IdempotencyKeys.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Ticketing.Api.Infrastructure;

public static class IdempotencyHeaders
{
    public const string Key = "Idempotency-Key";
    public const string Replayed = "Idempotent-Replayed";
}

public static class IdempotencyKeys
{
    /// <summary>
    /// The same caller and key always give the same id, on any instance, after any restart.
    /// Keys are scoped to the caller, so two users who happen to pick the same key get two tickets.
    /// </summary>
    public static Guid IdFor(string userId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\n{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
```

A SHA-256 hash of the user and key, cut to 16 bytes, is a GUID that nobody can predict without knowing the key. The user is part of the hash, so two people who happen to pick the same key get two tickets.

Now use it in `Features/Tickets/CreateTicket.cs`. Add `using Microsoft.AspNetCore.Mvc;` at the top, a parameter for the header, and the lookup before the ticket is built:

```csharp
    public static async Task<Results<Created<TicketResponse>, ProblemHttpResult>> Handle(
        CreateTicketRequest request, ITicketStore store, ClaimsPrincipal user, TicketLog log, HttpRequest http,
        [FromHeader(Name = IdempotencyHeaders.Key)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Requesters open tickets for themselves; agents can open them on anyone's behalf
        if (!user.IsAgent() && request.RequesterId != user.UserId())
            return Problems.Forbidden("You can only create tickets for yourself.");

        // With a key, the id comes from the key: a retry finds the ticket the first call made
        var id = Guid.NewGuid();
        if (idempotencyKey is not null)
        {
            id = IdempotencyKeys.IdFor(user.UserId(), idempotencyKey);
            if (await store.GetAsync(id, cancellationToken) is { } existing)
                return TypedResults.Created(http.LocationFor(existing.Id), existing.ToResponse());
        }

        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Id = id,
            Title = request.Title,
            // ...the rest is unchanged
```

`[FromHeader]` also puts the header into the OpenAPI document, so callers can see it exists.

### Checkpoint: a guard just went red

**5 of 11 passing.** `Same_key_twice_creates_one_ticket` passes now. But `Ticket_created_with_a_key_is_readable_right_away`, which passed before you started, **fails**: create a ticket with a key, follow its `Location`, and get a **404**.

Look at the order of calls. The handler asks the store for the ticket (to see if a retry already made it), gets "not found," then adds it. The store your handler talks to is the **caching** decorator from Lab 5, and it caches the "not found." Then the add goes straight through without touching the cache. For the next five minutes, the cache insists the ticket doesn't exist.

Lab 5's talk-it-over predicted exactly this: caching "not found" is harmless while the server picks brand-new ids, and bites as soon as a caller can choose one. It's worse than a 404, too: `Same_key_twice` only passes by luck. The second create also sees the cached "not found," and **overwrites** the first ticket with a new one under the same id.

---

## Step 2: Make adds evict, too (5 min)

In `Data/CachingTicketStore.cs`, make `AddAsync` evict, like the other writes:

```csharp
    // Adds evict too. Ids used to be brand new, so nothing could be cached under one yet.
    // Once a caller can pick the id (through an Idempotency-Key), a "not found" read
    // before the add is cached, and would hide the new ticket until it expired.
    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        await inner.AddAsync(ticket, cancellationToken);
        await cache.RemoveAsync(Key(ticket.Id), cancellationToken);
    }
```

### Checkpoint

**6 of 11 passing.** `After_the_key_expires_a_retry_still_makes_no_duplicate` passes too, even though nothing expires yet. Hold that thought until Step 4.

Layer 1 is done, and it's the one that guarantees "at most one ticket." What it can't do: tell the caller this was a retry, notice they reused a key for a **different** ticket, or stop two copies of the same request racing each other (both look up, both see nothing, both add).

---

## Step 3: Remember the answers (10 min)

The second layer remembers, per caller and key, two things: **what was asked** (a fingerprint of the body) and **what was answered** (the result). Create `src/Ticketing.Api/Infrastructure/IdempotencyStore.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Ticketing.Api.Infrastructure;

/// <summary>Bound from Idempotency.</summary>
public class IdempotencyOptions
{
    /// <summary>How long a completed response is kept for replay. 24 hours is the common choice.</summary>
    public int ExpirySeconds { get; set; } = 86_400;
    public int MaxKeyLength { get; set; } = 255;
}

public enum IdempotencyOutcome { Started, Replay, InProgress, Mismatch }

public record IdempotencyLookup(IdempotencyOutcome Outcome, IResult? Response = null);

/// <summary>
/// Remembers, per caller and key, what was asked (a fingerprint of the body) and what was answered.
/// In memory, so it's per instance and lost on restart; that's why the ticket id also comes from the key.
/// In production this lives in Redis or the database, with the same three operations.
/// </summary>
public class IdempotencyStore(IOptions<IdempotencyOptions> options, TimeProvider time)
{
    private record Entry(string Fingerprint, IResult? Response, DateTimeOffset ExpiresAt);

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Lock _gate = new();

    /// <summary>Claims the key, or says why not. Check and claim happen under one lock: no gap to race into.</summary>
    public IdempotencyLookup Begin(string scope, string fingerprint)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.TryGetValue(scope, out var entry) && entry.ExpiresAt > now)
            {
                
                if (entry.Fingerprint != fingerprint) return new(IdempotencyOutcome.Mismatch);
                if (entry.Response is null) return new(IdempotencyOutcome.InProgress);
                return new(IdempotencyOutcome.Replay, entry.Response);
            }

            // In flight until Complete or Release; the expiry only matters if the request never finishes
            _entries[scope] = new Entry(fingerprint, null, now.AddSeconds(options.Value.ExpirySeconds));
            return new(IdempotencyOutcome.Started);
        }
    }

    /// <summary>Keeps the answer, for every retry until it expires.</summary>
    public void Complete(string scope, IResult response)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.TryGetValue(scope, out var entry))
                _entries[scope] = entry with { Response = response, ExpiresAt = now.AddSeconds(options.Value.ExpirySeconds) };
        }
    }

    /// <summary>The request didn't succeed: forget the key, so a retry gets a real second attempt.</summary>
    public void Release(string scope)
    {
        lock (_gate) _entries.Remove(scope);
    }
}
```

Three operations, and each one is a decision:

- **`Begin`** checks and claims the key under one lock. A key not seen before (or expired) is claimed as **in flight**. A key in flight means the first call is still running. A key with an answer is a replay, **if** the fingerprint matches; a different fingerprint means the caller reused a key for a different request.
- **`Complete`** stores the answer, and restarts the clock: 24 hours from when the answer was known.
- **`Release`** forgets the key. A request that failed hasn't done anything to repeat, so the retry should get a real second attempt.

It takes `TimeProvider` instead of reading the clock, the fix Lab 8's talk-it-over suggested for `SlaMonitor`.

Add the problem types to `Infrastructure/Problems.cs`. In `ProblemTypes`, above `RateLimited`:

```csharp
    public const string IdempotencyKeyInvalid = Base + "idempotency-key-invalid";       // 400: empty or too long
    public const string IdempotencyKeyInProgress = Base + "idempotency-key-in-progress"; // 409: first try still running
    public const string IdempotencyKeyReused = Base + "idempotency-key-reused";         // 422: same key, different request
```

And at the end of `Problems`:

```csharp
    public static ProblemHttpResult IdempotencyKeyInvalid(int maxLength) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            type: ProblemTypes.IdempotencyKeyInvalid,
            title: "Invalid Idempotency-Key",
            detail: $"An Idempotency-Key must be 1 to {maxLength} characters. A new GUID per operation works well.");

    public static ProblemHttpResult IdempotencyKeyInProgress() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: ProblemTypes.IdempotencyKeyInProgress,
            title: "Request already in progress",
            detail: "A request with this Idempotency-Key is still being processed. Retry shortly to get its result.");

    public static ProblemHttpResult IdempotencyKeyReused() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            type: ProblemTypes.IdempotencyKeyReused,
            title: "Idempotency-Key reused",
            detail: "This Idempotency-Key was already used with a different request. Use a new key for a new operation.");
```

The status codes come from the draft: **409** for "in progress" (it's a conflict with a request that hasn't finished), **422** for "reused" (the request is well formed, but can't be processed with that key).

---

## Step 4: Wrap the endpoint in a filter (10 min)

Where should the check live? Not in the handler: it's the same logic for any POST you want to make retry-safe, and the handler shouldn't have to know replays exist. Not in middleware either: middleware sees raw bytes, before model binding, and runs for every route. An **endpoint filter** runs only on the endpoints that opt in, after binding, with the bound request object in hand.

Create `src/Ticketing.Api/Infrastructure/IdempotencyFilter.cs`:

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// Wraps a POST handler: the first call with a key runs the handler and keeps its answer;
/// a retry with the same key and body gets that answer back without running the handler again.
/// TRequest is the body type, so the filter can fingerprint what was actually bound.
/// </summary>
public class IdempotencyFilter<TRequest>(IdempotencyStore store, IOptions<IdempotencyOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // No key: an ordinary request, exactly as before
        if (!http.Request.Headers.TryGetValue(IdempotencyHeaders.Key, out var values))
            return await next(context);

        var key = values.ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > options.Value.MaxKeyLength)
            return Problems.IdempotencyKeyInvalid(options.Value.MaxKeyLength);

        var scope = $"{http.User.UserId()}\n{key}";
        var lookup = store.Begin(scope, Fingerprint(context.Arguments.OfType<TRequest>().FirstOrDefault()));

        switch (lookup.Outcome)
        {
            case IdempotencyOutcome.Replay:
                http.Response.Headers[IdempotencyHeaders.Replayed] = "true";
                return lookup.Response;
            case IdempotencyOutcome.Mismatch:
                return Problems.IdempotencyKeyReused();
            case IdempotencyOutcome.InProgress:
                return Problems.IdempotencyKeyInProgress();
        }

        try
        {
            var result = await next(context);

            // Keep successes only. A 4xx or 5xx releases the key: the caller fixes the request
            // (or waits out the outage) and tries again with the same key.
            if (result is IResult response && StatusOf(response) is >= 200 and < 300)
                store.Complete(scope, response);
            else
                store.Release(scope);

            return result;
        }
        catch
        {
            // Thrown exceptions become Problem Details later on; to the key, they're a failure
            store.Release(scope);
            throw;
        }
    }

    private static string Fingerprint(TRequest? request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    // Results<Created<T>, ProblemHttpResult> wraps the real result; look inside it
    private static int? StatusOf(IResult result) => result switch
    {
        INestedHttpResult nested => StatusOf(nested.Result),
        IStatusCodeHttpResult withStatus => withStatus.StatusCode,
        _ => null
    };
}
```

Worth a second look:

- **The fingerprint** is a hash of the **bound** request, serialized back to JSON. Whitespace or property order in the original body doesn't change it; a different title does.
- **Only 2xx answers are kept.** A 400 for a bad body, a 403, or a 503 because the store was down: none of them created anything, so none of them should be replayed forever. The `catch` handles the failures that arrive as exceptions (a store outage throws, and Lab 2's exception handlers turn it into a 503 later).
- **`INestedHttpResult`**: the handler returns `Results<Created<TicketResponse>, ProblemHttpResult>`, a wrapper. The status is on the result inside it.
- **The replay** returns the same `IResult` object the first call returned. Results are just descriptions of a response, so writing one twice gives the same status, headers, and body.

Create `src/Ticketing.Api/Infrastructure/IdempotencyExtensions.cs`:

```csharp
namespace Ticketing.Api.Infrastructure;

public static class IdempotencyExtensions
{
    public static IServiceCollection AddIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdempotencyOptions>(configuration.GetSection("Idempotency"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IdempotencyStore>();   // singleton: it must outlive any one request
        return services;
    }

    /// <summary>Makes a POST safe to retry with an Idempotency-Key header.</summary>
    public static RouteHandlerBuilder WithIdempotency<TRequest>(this RouteHandlerBuilder builder) =>
        builder
            .AddEndpointFilter<IdempotencyFilter<TRequest>>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
```

`AddEndpointFilter<T>` creates the filter through DI, so its constructor can ask for the store and options.

Opt the create endpoint in, in `CreateTicket.cs`:

```csharp
        group.MapPost("/", Handle)
            .WithName("CreateTicket")
            .RequireRateLimiting(RateLimitPolicies.CreateTicket)
            .WithIdempotency<CreateTicketRequest>();
```

Register it in `Program.cs`, after `AddTicketingResilience()`:

```csharp
builder.Services.AddIdempotency(builder.Configuration);
```

Add the settings to `appsettings.json`, before `"Jobs"`:

```json
  "Idempotency": {
    "ExpirySeconds": 86400,
    "MaxKeyLength": 255
  },
```

Last, the browser. The support site (Lab 4's CORS policy) has to be allowed to **send** the new header, and to **read** the new response header. In `Infrastructure/CorsExtensions.cs`:

```csharp
            .WithHeaders("Authorization", "Content-Type", ApiKeyDefaults.Header, "If-Match", "If-None-Match",
                IdempotencyHeaders.Key)
            // Browsers hide response headers from cross-origin pages unless they're listed here
            .WithExposedHeaders("X-Total-Count", "Link", "ETag", "Retry-After", "Location",
                IdempotencyHeaders.Replayed)));
```

### Checkpoint

**All 11 passing.** Run the whole suite too (`dotnet test`): every earlier lab still passes, since a request without a key never reaches the new code.

Now back to the thought from Step 2. `After_the_key_expires_a_retry_still_makes_no_duplicate` runs with a one-second expiry. The retry comes back as a fresh **201, not a replay**: the response cache has forgotten. It's still **the same ticket**, because the id came from the key. That's why there are two layers. The cache gives the nice behavior (exact replays, misuse detection); the key in the data gives the guarantee.

---

## Step 5: Try it (5 min)

Run the API and work through `http/A-idempotency.http`:

1. Create with a key: **201**.
2. Send exactly the same request again: the same **201**, the same id and `Location`, plus **`Idempotent-Replayed: true`**.
3. Same key, different title: **422** `idempotency-key-reused`.
4. Same key as erin: a **different** ticket.
5. A 260-character key: **400** `idempotency-key-invalid`.
6. Slow the store to 3 seconds (the `PUT /dev/chaos` request), change `@key`, and send the create twice, quickly: the second is a **409** `idempotency-key-in-progress`, and the first finishes with a 201. Reset chaos when you're done.

Then look at the traces panel in the lab client: a replay has **no store spans** at all. The handler never ran.

---

## Finished

Your `Program.cs` gained one line:

```csharp
builder.Services.AddTicketingResilience();
builder.Services.AddIdempotency(builder.Configuration);
builder.Services.AddTicketEvents();
```

| | What it gives you | What it can't do |
|---|---|---|
| **Key in the data** (Steps 1 and 2) | At most one ticket per key, across restarts and instances, forever | Tell a retry apart from a first call; catch a key reused for a different ticket; stop two concurrent copies racing |
| **Response cache** (Steps 3 and 4) | Exact replays, `Idempotent-Replayed`, 409 and 422 for misuse | Remember past its expiry, past a restart, or on another instance (in memory) |

Problem types added to the catalog: `idempotency-key-invalid` (400), `idempotency-key-in-progress` (409), `idempotency-key-reused` (422).

---

## Talk it over

### Concurrency needs the database's help

The filter's lock stops two copies of a request racing **on one instance**. On three instances, both copies can still look up the ticket, see nothing, and add it. With the id derived from the key, they at least add the **same** id, so the database's primary key turns the second insert into a conflict instead of a duplicate. That's the real guarantee: an insert that fails on a duplicate key (`INSERT` with a unique constraint, or `IfNotExists` in Cosmos DB), not a "look, then add." The in-memory store's `AddAsync` overwrites, which is why this lab leans on the filter for concurrency.

### A shared response cache

To make replays work across instances, `IdempotencyStore` moves to Redis or the database, keeping the same three operations. `Begin` becomes an atomic "set if not exists" with an expiry (`SET key value NX EX 86400` in Redis). You'd store the status, headers, and body rather than an `IResult` object, since objects don't serialize across machines.

### Which keys, and who makes them

The client makes the key, once per **operation**, not per **request**: generate a GUID when the user clicks "Submit," and send the same one on every retry of that click. A key per HTTP request defeats the point. A key derived from the content (a hash of the body) is tempting, but then two genuinely identical tickets can never be created.

### What about PUT, PATCH, and DELETE?

HTTP already defines PUT and DELETE as idempotent: doing them twice leaves the same state as doing them once. PATCH isn't, in general, but Lab 5's `If-Match` makes a retried PATCH fail with 412 instead of applying twice, which is a different route to the same safety. POST is the one that needs a key.

### Now Lab 7's rule can relax

Lab 7 refused to retry timed-out writes because they might have happened. A create **with an Idempotency-Key** can be retried safely, by the client or by an outbound resilience pipeline, because a repeat can't create anything new. Making operations idempotent is what turns "retry only reads" into "retry anything that carries a key."
