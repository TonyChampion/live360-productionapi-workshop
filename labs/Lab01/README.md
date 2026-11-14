# Lab 1: Controllers to Minimal APIs

**Module 1: Minimal APIs and Vertical Slices** | about 45 to 60 minutes

You start with the API most teams have today: controllers calling a service layer calling a store. You'll migrate it to minimal APIs organized as vertical slices, one feature at a time, while the tests prove callers can't tell anything changed. Then you'll add what the controller version never had: real request and response contracts, versioning, and an OpenAPI document.

**You're done when** `dotnet test` is fully green and `Program.cs` reads like a table of contents.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/Lab01/Start` in your editor and run the tests:

```
dotnet test
```

You should see **5 failures**, all in `ApiShapeTests`. That's the work for this lab.

Two groups of tests matter today:

- **`TicketEndpointTests`** (all passing). These were written against the controller API. They must stay green through every step. If one goes red, you changed something a caller can see.
- **`ApiShapeTests`** (5 red, 1 green). These describe what the API should look like when you're done. The one already passing (`Unsupported_api_version_is_rejected`) is a guard: it should stay green too.

Run just this lab's tests any time with:

```
dotnet test --filter "Lab=1"
```

---

## Step 1: Read what you have (3 min)

Open these and notice the shape:

- `Controllers/TicketsController.cs` and `Controllers/CommentsController.cs`: thin, attribute-routed, and they return the **domain model** (`Ticket`, `Comment`) straight to the caller
- `Services/TicketService.cs`: a "service layer" that mostly passes calls through to the store
- `Program.cs`: every registration inline, including the store and its decorator

Three layers, and most changes touch all three. Keep that in mind for the vertical slice discussion.

---

## Step 2: Move the wiring out of `Program.cs` (5 min)

Create `src/Ticketing.Api/Infrastructure/TicketingServiceExtensions.cs`:

```csharp
using Ticketing.Api.Chaos;
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

        // Decorator, registered by hand: callers ask for ITicketStore and get the
        // fault-injecting wrapper around the real store.
        services.AddSingleton<ITicketStore>(sp =>
            new FaultInjectingTicketStore(
                sp.GetRequiredService<InMemoryTicketStore>(),
                sp.GetRequiredService<ChaosSettings>()));

        return services;
    }
}
```

In `Program.cs`, replace the three store and chaos registrations with one line:

```csharp
builder.Services.AddTicketing();
```

Add `using Ticketing.Api.Infrastructure;` at the top. Leave everything else alone for now.

Run `dotnet test`. Nothing should change.

### Dependency injection, briefly

Everything in this API is wired through DI, so three rules are worth knowing before you go further.

**Lifetimes.** Singleton is one instance for the app. Scoped is one per request. Transient is a new one every time it's asked for. The store has to be a singleton because the store **is** the data.

**Decorators.** The built-in container has no `Decorate()` method, so the decorator is registered by hand with a factory. The library [Scrutor](https://github.com/khellang/Scrutor) adds one if you want it.

**Captive dependencies.** A singleton must never hold a scoped service, because the scoped service would live forever. This comes back in Lab 8.

### Try it: break the lifetime on purpose (optional, 3 min)

**Experiment A.** In `AddTicketing()`, change `AddSingleton<InMemoryTicketStore>()` to `AddScoped<InMemoryTicketStore>()` and run the tests.

Requests now fail with a 500. In Development, ASP.NET Core validates scopes and refuses to resolve a scoped service from inside a singleton. That's the captive dependency rule, caught for you.

**Experiment B.** Now also change `AddSingleton<ITicketStore>(sp => ...)` to `AddScoped<ITicketStore>(sp => ...)` and run the tests again.

No errors this time, and that's worse. Creating a ticket returns 201, then fetching it returns 404, because every request gets a brand-new empty store. Nothing warns you about this one.

Scoped isn't wrong in general. With a database behind the store, a scoped store is the normal choice, because the data lives in the database, not in the object. This store keeps its data in memory, in the instance itself, so it has to live as long as the app. **The lifetime follows where the state lives.**

**Put both back to `AddSingleton` before moving on.**

---

## Step 3: Define the contracts (5 min)

Right now the API accepts and returns `Ticket` directly. That means any property on the domain model is something a caller can send. Run this request from `http/01-structure.http`:

```http
POST {{host}}/api/v1/tickets
Content-Type: application/json

{
  "title": "Please fix",
  "description": "Sneaky fields.",
  "priority": "Low",
  "requesterId": "mallory",
  "assigneeId": "mallory",
  "status": "Closed"
}
```

Look at the response: Mallory just assigned the ticket to herself. That's **overposting**, and it's why contracts exist.

Create `src/Ticketing.Api/Features/Tickets/TicketContracts.cs`:

```csharp
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Tickets;

// What a caller may send. No Id, Status, or AssigneeId: the server owns those.
public record CreateTicketRequest(
    string Title,
    string Description,
    TicketPriority Priority,
    string RequesterId);

// What a caller gets back. Comments are their own resource; the ticket just counts them.
public record TicketResponse(
    Guid Id,
    string Title,
    string Description,
    TicketStatus Status,
    TicketPriority Priority,
    string RequesterId,
    string? AssigneeId,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class TicketMappings
{
    public static TicketResponse ToResponse(this Ticket t) => new(
        t.Id, t.Title, t.Description, t.Status, t.Priority,
        t.RequesterId, t.AssigneeId, t.Comments.Count, t.CreatedAt, t.UpdatedAt);
}
```

The request has no `Id`, `Status`, or `AssigneeId`, because the server owns those. The response counts comments instead of embedding them, since comments are their own resource.

---

## Step 4: Build the ticket slices (10 min)

Each feature gets one file containing its route **and** its logic. No service layer.

First, a small helper for the `Location` header. Create `src/Ticketing.Api/Infrastructure/LocationExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Http.Extensions;

namespace Ticketing.Api.Infrastructure;

public static class LocationExtensions
{
    /// <summary>
    /// Absolute URL of a resource created under the current request path,
    /// e.g. POST https://host/api/v1/tickets -> https://host/api/v1/tickets/{id}.
    /// Absolute, because that's what the controller API returned and callers may rely on it.
    /// </summary>
    public static Uri LocationFor(this HttpRequest request, object id)
        => new($"{request.GetEncodedUrl().TrimEnd('/')}/{id}");
}
```

> **Why absolute?** The controller version used `CreatedAtAction`, which returns an absolute URL. If you return a relative one instead, `Create_returns_201_with_location` goes red. That's the tests doing their job: a caller reading `Location` would break, and you'd never have noticed by eye.

Now the three slices, in `src/Ticketing.Api/Features/Tickets/`.

`ListTickets.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;

namespace Ticketing.Api.Features.Tickets;

public static class ListTickets
{
    public static RouteGroupBuilder MapListTickets(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListTickets");
        return group;
    }

    public static async Task<Ok<List<TicketResponse>>> Handle(
        ITicketStore store, CancellationToken cancellationToken)
    {
        var tickets = await store.ListAsync(cancellationToken);
        return TypedResults.Ok(tickets.Select(t => t.ToResponse()).ToList());
    }
}
```

`GetTicket.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;

namespace Ticketing.Api.Features.Tickets;

public static class GetTicket
{
    public static RouteGroupBuilder MapGetTicket(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}", Handle).WithName("GetTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, NotFound>> Handle(
        Guid id, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        return ticket is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ticket.ToResponse());
    }
}
```

`CreateTicket.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class CreateTicket
{
    public static RouteGroupBuilder MapCreateTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/", Handle).WithName("CreateTicket");
        return group;
    }

    public static async Task<Created<TicketResponse>> Handle(
        CreateTicketRequest request, ITicketStore store, HttpRequest http, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            RequesterId = request.RequesterId,
            Status = TicketStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };

        await store.AddAsync(ticket, cancellationToken);

        // Location built from the request URL, so it stays right when v2 arrives
        return TypedResults.Created(http.LocationFor(ticket.Id), ticket.ToResponse());
    }
}
```

A few things to notice:

- **Services arrive as handler parameters.** No constructor. ASP.NET Core sees `ITicketStore` is registered in DI and injects it. You only need `[FromServices]` when a type could be mistaken for something bound from the request.
- **`TypedResults` and `Results<...>`** make the return types part of the method signature. The handler is a plain static method you can unit test, and OpenAPI can read every possible response.
- **`CancellationToken`** is bound automatically to the request's abort signal. Pass it through now; it pays off in Lab 7.

---

## Step 5: Map the tickets and retire the first controller (5 min)

Create `src/Ticketing.Api/Features/TicketingEndpoints.cs`. For now, comments are left out and the group is a plain `/api/v1`:

```csharp
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Features;

public static class TicketingEndpoints
{
    public static IEndpointRouteBuilder MapTicketingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGroup("/tickets")
            .WithTags("Tickets")
            .MapListTickets()
            .MapGetTicket()
            .MapCreateTicket();

        return app;
    }
}
```

Create `src/Ticketing.Api/Infrastructure/ApiServiceExtensions.cs` with just the JSON setup for now:

```csharp
using System.Text.Json.Serialization;

namespace Ticketing.Api.Infrastructure;

public static class ApiServiceExtensions
{
    public static IServiceCollection AddApiDefaults(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services;
    }
}
```

**Delete `Controllers/TicketsController.cs`.** Two endpoints can't own the same route, so the controller has to go the moment its minimal replacement is mapped.

Update `Program.cs`. The comments controller is still running, so MVC and the service stay registered:

```csharp
using System.Text.Json.Serialization;
using Ticketing.Api.Chaos;
using Ticketing.Api.Features;
using Ticketing.Api.Infrastructure;
using Ticketing.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Still here: CommentsController hasn't moved yet
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<ITicketService, TicketService>();

builder.Services.AddApiDefaults();
builder.Services.AddTicketing();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapTicketingEndpoints();
app.MapHealthChecks("/health");
app.MapChaosEndpoints();

app.Run();

public partial class Program;
```

### Checkpoint: both styles in one app

```
dotnet test
```

Expect **3 failures**: `No_controllers_remain`, `Responses_report_supported_api_versions`, and `OpenApi_document_lists_every_ticket_endpoint`. Every `TicketEndpointTests` test should pass.

Look at what just happened. Tickets run on minimal APIs, comments still run on a controller, and the callers can't tell. This is how you migrate a real codebase: one feature at a time, with the tests as the safety net. Both contract tests (overposting and `commentCount`) are already green too.

---

## Step 6: Migrate comments and retire MVC (5 min)

Create the comment slices in `src/Ticketing.Api/Features/Comments/`.

`CommentContracts.cs`:

```csharp
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Comments;

public record AddCommentRequest(string AuthorId, string Body);

public record CommentResponse(Guid Id, Guid TicketId, string AuthorId, string Body, DateTimeOffset CreatedAt);

public static class CommentMappings
{
    public static CommentResponse ToResponse(this Comment c) =>
        new(c.Id, c.TicketId, c.AuthorId, c.Body, c.CreatedAt);
}
```

`ListComments.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;

namespace Ticketing.Api.Features.Comments;

public static class ListComments
{
    public static RouteGroupBuilder MapListComments(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListComments");
        return group;
    }

    public static async Task<Results<Ok<List<CommentResponse>>, NotFound>> Handle(
        Guid ticketId, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(ticketId, cancellationToken);
        return ticket is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ticket.Comments.Select(c => c.ToResponse()).ToList());
    }
}
```

`AddComment.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Comments;

public static class AddComment
{
    public static RouteGroupBuilder MapAddComment(this RouteGroupBuilder group)
    {
        group.MapPost("/", Handle).WithName("AddComment");
        return group;
    }

    public static async Task<Results<Created<CommentResponse>, NotFound>> Handle(
        Guid ticketId, AddCommentRequest request, ITicketStore store, HttpRequest http,
        CancellationToken cancellationToken)
    {
        var comment = new Comment
        {
            AuthorId = request.AuthorId,
            Body = request.Body,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var added = await store.AddCommentAsync(ticketId, comment, cancellationToken);
        return added is null
            ? TypedResults.NotFound()
            : TypedResults.Created(http.LocationFor(added.Id), added.ToResponse());
    }
}
```

In `TicketingEndpoints.cs`, add `using Ticketing.Api.Features.Comments;` and map the comments group under the tickets group:

```csharp
        api.MapGroup("/tickets/{ticketId:guid}/comments")
            .WithTags("Comments")
            .MapListComments()
            .MapAddComment();
```

Now **delete the `Controllers` folder and the `Services` folder.** Then remove MVC from `Program.cs`: the `AddControllers()` call, the `ITicketService` registration, `app.MapControllers()`, and the two `using` lines that no longer resolve.

Run `dotnet test`. Expect **2 failures**, both about versioning and OpenAPI.

---

## Step 7: Version from day one (5 min)

Versioning goes in **after** the migration on purpose. Once the versioning library is registered, it rejects endpoints that have no version information, and the comments controller would have broken in the middle of Step 5.

Add the packages to `src/Ticketing.Api/Ticketing.Api.csproj` (versions are already pinned in `Directory.Packages.props`):

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Asp.Versioning.Http" />
    <PackageReference Include="Asp.Versioning.OpenApi" />
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
  </ItemGroup>
</Project>
```

Replace `ApiServiceExtensions.cs` with the full version:

```csharp
using System.Text.Json.Serialization;
using Asp.Versioning;

namespace Ticketing.Api.Infrastructure;

public static class ApiServiceExtensions
{
    /// <summary>
    /// Cross-cutting API plumbing: JSON, versioning, and the OpenAPI document.
    /// </summary>
    public static IServiceCollection AddApiDefaults(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.ReportApiVersions = true;                      // api-supported-versions header
                o.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'V";                     // document name: v1
                o.SubstituteApiVersionInUrl = true;             // /api/v{version}/... shows as /api/v1/...
            })
            .AddOpenApi();

        return services;
    }
}
```

In `TicketingEndpoints.cs`, replace the plain group with a versioned one. Add `using Asp.Versioning;` and change:

```csharp
        var api = app.MapGroup("/api/v1");
```

to:

```csharp
        // Every route lives under a version from day one, even with only v1
        var api = app.NewVersionedApi("Ticketing")
            .MapGroup("/api/v{version:apiVersion}")
            .HasApiVersion(new ApiVersion(1, 0));
```

The URLs don't change, so the migration tests still pass. What you gain: every response now carries an `api-supported-versions` header, an unknown version like `/api/v2/tickets` returns 404, and adding v2 later is a new group rather than `if` statements inside v1's handlers.

> **Why version an API that only has one version?** Because the day you need v2, every existing caller is already pinned to v1, and nobody has to touch their code. Teams that skip this end up bolting versioning on later with conditionals scattered through their handlers.

---

## Step 8: Publish the OpenAPI document, and a UI for it (4 min)

In `Program.cs`, add this line before `app.MapTicketingEndpoints();`:

```csharp
app.MapOpenApi().WithDocumentPerVersion();
```

Open `/openapi/v1.json` in the browser. Every endpoint is listed, with its request contract and every possible response, because `TypedResults` put them in the method signatures.

That's the document, but nothing to click. Since .NET 9, ASP.NET Core generates OpenAPI itself (`Microsoft.AspNetCore.OpenApi`) and no longer ships Swagger UI in the template, so the UI is a separate choice. We'll use Swagger UI on its own, without the rest of Swashbuckle: the built-in generator makes the document, and Swagger UI only renders it.

Add the package to `src/Ticketing.Api/Ticketing.Api.csproj` (the version is already pinned):

```xml
    <PackageReference Include="Swashbuckle.AspNetCore.SwaggerUI" />
```

Then, right after the `MapOpenApi` line:

```csharp
if (app.Environment.IsDevelopment())
{
    // ASP.NET Core generates the document but ships no UI. Swagger UI renders it at /swagger.
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "v1"));
}
```

Run the API and refresh the lab client. An **API docs** button appears in the header (the client checks for `/swagger` and shows the button once it answers). It opens Swagger UI in a new tab, where you can expand any endpoint, see its contract, and send requests with **Try it out**.

> **Why only in Development?** The UI is a convenience for the people building and calling the API, not something production needs to serve. Keeping it out of production also keeps an interactive request console off your public surface. The document itself is a separate decision: some teams publish it so clients can generate code from it.

---

## Finished

Your `Program.cs` should now look like this:

```csharp
using Ticketing.Api.Chaos;
using Ticketing.Api.Features;
using Ticketing.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiDefaults();
builder.Services.AddTicketing();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi().WithDocumentPerVersion();
if (app.Environment.IsDevelopment())
{
    // ASP.NET Core generates the document but ships no UI. Swagger UI renders it at /swagger.
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "v1"));
}
app.MapTicketingEndpoints();
app.MapHealthChecks("/health");
app.MapChaosEndpoints();

app.Run();

public partial class Program;
```

And `TicketingEndpoints.cs`, the whole public surface of the API in one file:

```csharp
using Asp.Versioning;
using Ticketing.Api.Features.Comments;
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Features;

public static class TicketingEndpoints
{
    /// <summary>
    /// The whole public surface of the API, in one place.
    /// </summary>
    public static IEndpointRouteBuilder MapTicketingEndpoints(this IEndpointRouteBuilder app)
    {
        // Every route lives under a version from day one, even with only v1
        var api = app.NewVersionedApi("Ticketing")
            .MapGroup("/api/v{version:apiVersion}")
            .HasApiVersion(new ApiVersion(1, 0));

        api.MapGroup("/tickets")
            .WithTags("Tickets")
            .MapListTickets()
            .MapGetTicket()
            .MapCreateTicket();

        api.MapGroup("/tickets/{ticketId:guid}/comments")
            .WithTags("Comments")
            .MapListComments()
            .MapAddComment();

        return app;
    }
}
```

Run everything:

```
dotnet test
```

**All green.** Then run the API (`dotnet run --project src/Ticketing.Api`), open the client at https://localhost:7001 (use **API docs** to open Swagger UI), and work through `http/01-structure.http`. Compare the overposting request and the single-ticket response with what you saw at the start of the lab.

---

## Talk it over: vertical slices

You moved from layers (controller, service, store) to slices (one folder per feature, each owning its endpoint, contract, and logic).

**What slices get you**
- A change usually touches one folder
- Features don't depend on each other, so they're easy to add, change, or delete
- No pass-through service layer
- A natural fit for minimal APIs

**What they cost**
- Some duplication between slices (each mapping its own response, for example)
- Logic shared by several slices needs a deliberate home, or it gets copied
- Without team agreement on conventions, slices drift into different styles
- Unfamiliar at first to teams used to layers

**When controllers still make sense**
- Large existing controller codebases where the migration cost outweighs the benefit
- Teams that rely on MVC features like model binding conventions, filters, and `[ApiController]` behaviors across many endpoints
- Mixed MVC apps where views and APIs share a lot of infrastructure

As you saw in Step 5, you don't have to choose all at once. Both styles run side by side in one app.
