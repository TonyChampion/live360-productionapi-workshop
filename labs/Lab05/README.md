# Lab 5: Make It Fast, Keep It Right

**Module 5: Performance** | about 60 to 70 minutes

The demo version returns every ticket on every list call and reads the store on every request. That's fine with 10 tickets and a store that answers in a microsecond. In this lab the chaos panel finally earns its keep: you'll make the store slow, then make the API fast anyway, without serving anyone stale data or someone else's data.

**You're done when** lists are paged and filterable, single-ticket reads come from a cache that stays correct, callers can skip downloading a ticket they already have, and two people editing the same ticket can't silently overwrite each other.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/Lab05/Start` and run this lab's tests:

```
dotnet test --filter "Lab=5"
```

**11 failures, 2 passing.** The 2 passing are guards: changes must invalidate the cache, and caching must never let bob read alice's ticket. Right now they pass because there's no cache. They must still pass when there is.

### What's new in your Start folder

- **The lab client has a "Cache miss vs hit" run case.** It turns on 800 ms of store latency, creates a ticket, and reads it twice.
- **`http/05-performance.http`** covers paging, filters, ETags, and the chaos settings for this lab.
- **The `HybridCache` package version** is pinned in `Directory.Packages.props`.

---

## Step 1: Page and filter the list (10 min)

An unbounded list is a time bomb. It's fast in the demo and takes the API down the day a customer has 200,000 tickets.

There's a catch, though: `GET /api/v1/tickets` returns a JSON **array** today, and callers depend on that. Wrapping it in `{ "items": [...], "total": ... }` would break every one of them. So the v1 list keeps its array and gains:

- **Optional** query parameters: `page`, `pageSize`, `status`, `priority`, `assigneeId`
- An **`X-Total-Count`** header with the total matching tickets
- A **`Link`** header ([RFC 8288](https://www.rfc-editor.org/rfc/rfc8288)) pointing to the next and previous pages

That's how GitHub's API pages, and it's a non-breaking change: a caller that sends no parameters still gets an array.

Replace `Features/Tickets/ListTickets.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

/// <summary>
/// Query string parameters. Every one is optional (nullable), so existing callers keep working
/// unchanged. A class with properties rather than a positional record: OpenAPI generation can't
/// yet read validation attributes on record constructor parameters bound with [AsParameters].
/// </summary>
public class ListTicketsQuery
{
    public const int DefaultPageSize = 25;

    // Explicit names keep the query string (and the OpenAPI document) camelCase
    [FromQuery(Name = "page"), Range(1, int.MaxValue)]
    public int? Page { get; init; }

    [FromQuery(Name = "pageSize"), Range(1, 100)]
    public int? PageSize { get; init; }

    [FromQuery(Name = "status")]
    public TicketStatus? Status { get; init; }

    [FromQuery(Name = "priority")]
    public TicketPriority? Priority { get; init; }

    [FromQuery(Name = "assigneeId")]
    public string? AssigneeId { get; init; }
}

public static class ListTickets
{
    public static RouteGroupBuilder MapListTickets(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListTickets");
        return group;
    }

    public static async Task<Ok<List<TicketResponse>>> Handle(
        [AsParameters] ListTicketsQuery query, ITicketStore store, ClaimsPrincipal user,
        HttpContext http, CancellationToken cancellationToken)
    {
        var tickets = await store.ListAsync(cancellationToken);

        // Agents see everything; requesters see only their own tickets
        IEnumerable<Ticket> visible = user.IsAgent()
            ? tickets
            : tickets.Where(t => t.RequesterId == user.UserId());

        if (query.Status is not null) visible = visible.Where(t => t.Status == query.Status);
        if (query.Priority is not null) visible = visible.Where(t => t.Priority == query.Priority);
        if (query.AssigneeId is not null) visible = visible.Where(t => t.AssigneeId == query.AssigneeId);

        // A stable order (with a unique tiebreaker) is what makes paging correct:
        // without it, the same ticket can appear on two pages, or on none
        var ordered = visible.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();

        var pageNumber = query.Page ?? 1;
        var pageSize = query.PageSize ?? ListTicketsQuery.DefaultPageSize;
        var page = ordered
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(t => t.ToResponse())
            .ToList();

        http.Response.Headers["X-Total-Count"] = ordered.Count.ToString();
        var links = PageLinks(http.Request, pageNumber, pageSize, ordered.Count);
        if (links.Count > 0)
            http.Response.Headers.Link = string.Join(", ", links);

        return TypedResults.Ok(page);
    }

    /// <summary>RFC 8288 Link header entries for the neighboring pages.</summary>
    private static List<string> PageLinks(HttpRequest request, int page, int pageSize, int total)
    {
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var links = new List<string>();
        if (page < lastPage) links.Add($"<{UrlForPage(request, page + 1)}>; rel=\"next\"");
        if (page > 1) links.Add($"<{UrlForPage(request, page - 1)}>; rel=\"prev\"");
        return links;
    }

    private static string UrlForPage(HttpRequest request, int page)
    {
        var parameters = request.Query
            .Where(q => !q.Key.Equals("page", StringComparison.OrdinalIgnoreCase))
            .Select(q => KeyValuePair.Create(q.Key, q.Value.ToString()))
            .Append(KeyValuePair.Create("page", page.ToString()));

        return UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path,
            QueryString.Create(parameters!));
    }
}
```

Things to notice:

- **`[AsParameters]`** binds a whole object from the query string, and the `[Range]` attributes get validated by Lab 2's `AddValidation()`. `pageSize=500` is a 400 with no extra code.
- **The properties are nullable**, so they show up as optional in the OpenAPI document, and the defaults are applied in the handler.
- **The order has a unique tiebreaker** (`ThenBy(t => t.Id)`). Without one, two tickets created in the same instant can swap places between requests, and a caller paging through sees one twice and the other never. The test `Walking_every_page_returns_each_ticket_exactly_once` checks exactly that.

> **Why a class and not a record?** Everywhere else, the contracts are records. Here, OpenAPI generation currently crashes on validation attributes on a positional record bound with `[AsParameters]`. A class with properties works. A Lab 1 test caught this when it was first built: adding paging broke the OpenAPI document, five labs later.

### Checkpoint

**7 passing.** Send the paging requests in `05-performance.http` and look at the headers.

---

## Step 2: Cache single-ticket reads (10 min)

Now make the store slow: in the lab client, click **Slow (800 ms)** and click **Refresh**. Every request takes the full 800 ms. Reads vastly outnumber writes on a ticket, so cache them.

### Why not output caching?

ASP.NET Core's output cache stores whole HTTP responses. It's great for public content, and dangerous here: a cached response to dana's request could be served to bob. That's why it skips any request with an `Authorization` header by default. **Cache the data, not the response**, and let authorization run on every request.

### A third decorator

You've already seen this shape twice: `FaultInjectingTicketStore` wraps `InMemoryTicketStore`, and both are `ITicketStore`. Caching is one more wrapper.

Add the package to `src/Ticketing.Api/Ticketing.Api.csproj`:

```xml
    <PackageReference Include="Microsoft.Extensions.Caching.Hybrid" />
```

Create `src/Ticketing.Api/Data/CachingTicketStore.cs`:

```csharp
using Microsoft.Extensions.Caching.Hybrid;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

/// <summary>
/// Third decorator on the same interface. Single-ticket reads come from the cache;
/// every write evicts that ticket, so the next read goes back to the store.
///
/// It caches data, not HTTP responses, so authorization still runs on every request.
/// </summary>
public class CachingTicketStore(ITicketStore inner, HybridCache cache) : ITicketStore
{
    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private static string Key(Guid id) => $"ticket:{id}";

    public async Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await cache.GetOrCreateAsync(
            Key(id),
            async token => await inner.GetAsync(id, token),
            Entry,
            cancellationToken: cancellationToken);

    // Lists vary by caller, filter, and page, and change whenever any ticket does. Not cached.
    public Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default) =>
        inner.ListAsync(cancellationToken);

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        inner.AddAsync(ticket, cancellationToken);

    public async Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        await inner.UpdateAsync(ticket, cancellationToken);
        await cache.RemoveAsync(Key(ticket.Id), cancellationToken);
    }

    public async Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default)
    {
        var added = await inner.AddCommentAsync(ticketId, comment, cancellationToken);
        await cache.RemoveAsync(Key(ticketId), cancellationToken);
        return added;
    }
}
```

`HybridCache` keeps entries in memory and, if you add a distributed cache like Redis, there too. It also guarantees that when 50 requests miss on the same key at once, only one of them goes to the store (stampede protection).

Register it in `Infrastructure/TicketingServiceExtensions.cs`:

```csharp
using Ticketing.Api.Chaos;
using Microsoft.Extensions.Caching.Hybrid;
using Ticketing.Api.Data;

namespace Ticketing.Api.Infrastructure;

public static class TicketingServiceExtensions
{
    /// <summary>
    /// Data access for the ticketing feature: the in-memory store, wrapped by fault injection.
    /// </summary>
    public static IServiceCollection AddTicketing(this IServiceCollection services)
    {
        // Singleton: the store IS the data. Scoped would hand every request an empty store.
        services.AddSingleton<ChaosSettings>();
        services.AddSingleton<InMemoryTicketStore>();

        services.AddHybridCache();

        // Decorators, registered by hand. Order matters: the cache sits OUTSIDE the
        // fault injector, so a cached ticket can still be served while the store is down.
        //   caller -> CachingTicketStore -> FaultInjectingTicketStore -> InMemoryTicketStore
        services.AddSingleton<ITicketStore>(sp =>
            new CachingTicketStore(
                new FaultInjectingTicketStore(
                    sp.GetRequiredService<InMemoryTicketStore>(),
                    sp.GetRequiredService<ChaosSettings>()),
                sp.GetRequiredService<HybridCache>()));

        return services;
    }
}
```

**The order of the decorators matters.** The cache sits outside the fault injector, so it never sees injected latency or outages for a ticket it already has. Swap them and the cache would be useless during exactly the incidents it should help with.

### Checkpoint

**8 passing.** Run **Cache miss vs hit** in the lab client: the miss takes about 800 ms, the hit takes a few. Then click **Down** and open a ticket you've already viewed: it still loads. The list doesn't, because lists aren't cached.

---

## Step 3: Skip the download with ETags (10 min)

An **ETag** is a version identifier for a resource. The server sends it with the response; the client sends it back with `If-None-Match` next time. If nothing changed, the server answers **304 Not Modified** with no body.

Create `src/Ticketing.Api/Infrastructure/ETags.cs`:

```csharp
using Microsoft.Net.Http.Headers;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// An ETag identifies one version of a resource. Any change to the ticket (including a new
/// comment) moves UpdatedAt, so the tag changes with it.
/// </summary>
public static class ETags
{
    public static EntityTagHeaderValue For(Ticket ticket) => new($"\"{ticket.UpdatedAt.UtcTicks:x}\"");

    public static void SetETag(this HttpResponse response, EntityTagHeaderValue etag) =>
        response.GetTypedHeaders().ETag = etag;

    /// <summary>GET: does the caller already have this version? (If-None-Match)</summary>
    public static bool HasCurrentVersion(this HttpRequest request, EntityTagHeaderValue etag) =>
        request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false));

    /// <summary>PATCH: is the caller's change based on an old version? (If-Match)</summary>
    public static bool IsBasedOnStaleVersion(this HttpRequest request, EntityTagHeaderValue etag)
    {
        var ifMatch = request.GetTypedHeaders().IfMatch;
        return ifMatch.Count > 0 &&
               !ifMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: true));
    }
}
```

Update `Features/Tickets/GetTicket.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class GetTicket
{
    public static RouteGroupBuilder MapGetTicket(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}", Handle).WithName("GetTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, StatusCodeHttpResult, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, ClaimsPrincipal user, IAuthorizationService auth,
        HttpContext http, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!(await auth.AuthorizeAsync(user, ticket, Policies.TicketAccess)).Succeeded)
            return Problems.Forbidden($"You don't have access to ticket {ticket.Id}.");

        var etag = ETags.For(ticket);
        http.Response.SetETag(etag);

        // The caller already has this version: say so, and skip the body
        if (http.Request.HasCurrentVersion(etag))
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);

        return TypedResults.Ok(ticket.ToResponse());
    }
}
```

The authorization check still runs **before** the 304. Bob can't use `If-None-Match` to probe whether alice's ticket changed.

### Checkpoint

**11 passing.**

---

## Step 4: Don't let updates collide (10 min)

Alice and dana both open ticket 1111. Dana sets the priority to Urgent. A minute later alice, still looking at her old copy, sets it to Low. Dana's change is gone, and nobody knows. That's a **lost update**.

The fix uses the same ETag. A client sends **`If-Match`** with the version its change is based on. If the ticket has changed since, the server refuses with **412 Precondition Failed**, and the client re-reads and decides. It's called optimistic concurrency: assume no conflict, check at the moment of writing.

Add a problem type to `ProblemTypes` in `Infrastructure/Problems.cs`:

```csharp
    public const string PreconditionFailed = Base + "precondition-failed"; // 412: changed since you read it
```

And a factory to `Problems`:

```csharp
    public static ProblemHttpResult PreconditionFailed(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            type: ProblemTypes.PreconditionFailed,
            title: "The ticket has changed",
            detail: $"Ticket {id} was changed after you read it. Get the latest version and try again.");
```

Update `Features/Tickets/UpdateTicket.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

// Only the fields a caller is changing; nulls are left alone.
public record UpdateTicketRequest(
    [MinLength(1), StringLength(200)] string? Title,
    [StringLength(4000)] string? Description,
    Domain.TicketPriority? Priority);

public static class UpdateTicket
{
    public static RouteGroupBuilder MapUpdateTicket(this RouteGroupBuilder group)
    {
        group.MapPatch("/{id:guid}", Handle).WithName("UpdateTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, UpdateTicketRequest request, ITicketStore store, ClaimsPrincipal user,
        IAuthorizationService auth, HttpContext http, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!(await auth.AuthorizeAsync(user, ticket, Policies.TicketAccess)).Succeeded)
            return Problems.Forbidden($"You don't have access to ticket {ticket.Id}.");

        // Optimistic concurrency: if the caller sent If-Match, their change must be based on
        // the current version, or it would silently overwrite someone else's change
        if (http.Request.IsBasedOnStaleVersion(ETags.For(ticket)))
            return Problems.PreconditionFailed(id);

        if (request.Title is not null) ticket.Title = request.Title;
        if (request.Description is not null) ticket.Description = request.Description;
        if (request.Priority is not null) ticket.Priority = request.Priority.Value;

        await store.UpdateAsync(ticket, cancellationToken);
        http.Response.SetETag(ETags.For(ticket));
        return TypedResults.Ok(ticket.ToResponse());
    }
}
```

`If-Match` is optional, so existing callers keep working. Callers that care about lost updates opt in.

### Checkpoint

**All 13 passing.**

---

## Step 5: Let browsers see the new headers (2 min)

Browsers hide most response headers from cross-origin pages unless the API lists them. The support site from Lab 4 couldn't read `X-Total-Count`, `Link`, or `ETag` yet. In `Infrastructure/CorsExtensions.cs`, update the policy:

```csharp
            .WithHeaders("Authorization", "Content-Type", ApiKeyDefaults.Header, "If-Match", "If-None-Match")
            // Browsers hide response headers from cross-origin pages unless they're listed here
            .WithExposedHeaders("X-Total-Count", "Link", "ETag", "Retry-After")));
```

No test covers this one (a test client isn't a browser), which is exactly why it's easy to forget.

---

## Finished

```
dotnet test
```

**All green.** Work through `http/05-performance.http`.

---

## Talk it over

### Envelope or headers?

Many APIs return `{ "items": [...], "totalCount": 312, "next": "..." }`. It's easier for clients to read than headers, and many teams prefer it. Here it would have broken every v1 caller, so it's a **v2** change. That's the payoff of versioning on day one: when you want the envelope, you add a v2 list endpoint, deprecate v1's on a schedule (Lab 2), and nobody's integration breaks on a Tuesday.

### Offset or cursor paging?

`page` and `pageSize` (offset paging) are simple and let a client jump to page 7. They get slow on large tables (the database still walks past every skipped row), and items shift between pages when tickets are added mid-walk. **Cursor paging** (`?after=<last id seen>`) is fast at any depth and stable under inserts, but you can only go forward. Large, busy collections usually end up with cursors.

### What to cache, and for how long

- **Cache things read far more often than written**, keyed by something stable.
- **Invalidate on every write you own**, as the decorator does. Expiry is the safety net for writes you don't see (another instance, a background job, someone editing the database).
- **Multiple instances** need a distributed cache (Redis) or each instance holds its own stale copy after another one writes. `HybridCache` supports that with one more registration.
- **Not caching negative results** matters too. This decorator caches "not found" for a ticket ID until it expires. Server-generated GUIDs make that harmless here; with client-chosen IDs, it wouldn't be.

### Output caching, when it fits

Output caching shines for anonymous, public responses: a product catalog, a status page, a public OpenAPI document. If you ever turn it on for authenticated requests, the cache key **must** include the user (`VaryByValue` on the user id), or you've built a data leak.
