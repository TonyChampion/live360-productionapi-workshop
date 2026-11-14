# Lab 3: Who's Calling, and What Can They Do?

**Module 3: Authentication and Authorization** | about 45 to 60 minutes

Right now anyone can read, change, and close any ticket. In this lab you'll add sign-in with JWT bearer tokens, then the rules a support desk actually needs: agents can do anything, and requesters can only see and work with their own tickets. Last, a second way in for callers that aren't people: API keys.

Two words that get mixed up all the time:

- **Authentication**: who are you? Failing it is **401 Unauthorized** (the name is a historical accident; it means "unauthenticated").
- **Authorization**: are you allowed to do this? Failing it is **403 Forbidden**.

**You're done when** every ticket endpoint requires a token or an API key, the rules below hold, and `dotnet test` is fully green.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## The rules

| Action | Requester | Agent / Admin |
|---|---|---|
| List tickets | Only their own | All |
| Read, update, comment on a ticket | Only their own | Any |
| Create a ticket | Only for themselves | For anyone |
| Assign or close a ticket | Never | Yes |
| Health check and the lab client | Anyone, no token needed | |

---

## Before you start

Open `labs/Lab03/Start` and run this lab's tests:

```
dotnet test --filter "Lab=3"
```

**10 failures, 8 passing.** The passing ones are things that *should* be allowed ("an agent can read any ticket", "a valid API key gets in"). They pass now because nothing is locked. They're there to make sure you don't over-lock.

### What's new in your Start folder

- **The test fixture signs in for you.** From this lab on, `factory.CreateClient()` sends a token for dana, an agent, so every earlier lab's tests keep passing once auth is required. `CreateClientAs("alice", "requester")` and `CreateAnonymousClient()` cover everything else. Tests mint their own tokens with a test key, so they never depend on your machine.
- **The lab client has a "Signed in as" picker** and a **Set token** button.
- **The API project has a `UserSecretsId`**, shared by every lab folder. Tokens you create now keep working when you move to Lab 4.
- **`http/03-auth.http`** has a request for every rule above.

---

## Step 1: Create some tokens (5 min)

`dotnet user-jwts` is part of the .NET SDK. It creates a signing key for your machine and issues tokens your API will trust in Development, so you can test auth without setting up an identity provider.

From the API project folder:

```
cd src/Ticketing.Api
dotnet user-jwts create --name alice --role requester
dotnet user-jwts create --name bob --role requester
dotnet user-jwts create --name dana --role agent
```

Each command prints a token. Paste them into the variables at the top of `http/03-auth.http`. In the lab client, pick a user from **Signed in as** and paste the matching token when it asks.

> **Catching up, or tokens expired?** Every folder from here on has `make-tokens.ps1` (Windows) and `make-tokens.sh` (macOS/Linux) next to the solution. Run one (`./make-tokens.ps1` or `bash make-tokens.sh`) and it writes fresh tokens for alice, bob, dana, and an admin to `tokens.md`, ready to paste into the client and the `.http` files. Do it by hand once first, though: seeing what `user-jwts` creates is the point of this step.

Now look at what changed in `appsettings.Development.json`:

```json
"Authentication": {
  "Schemes": {
    "Bearer": {
      "ValidAudiences": [
        "https://localhost:7001",
        "http://localhost:5001"
      ],
      "ValidIssuer": "dotnet-user-jwts"
    }
  }
}
```

That's the configuration the API will use to validate tokens. The signing key itself is stored in your user secrets, not in the repo.

> **Look inside a token.** Paste one into [jwt.io](https://jwt.io) (fine for these throwaway dev tokens, never for real ones). You'll see `sub` and `unique_name` (the user), `role`, `aud` (who it's for), and `iss` (who issued it). A JWT is signed, not encrypted: anyone can read it, nobody can change it without breaking the signature.

---

## Step 2: Require a signed-in user (10 min)

Add the JWT bearer package to `src/Ticketing.Api/Ticketing.Api.csproj` (the version is already pinned):

```xml
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
```

Create `src/Ticketing.Api/Infrastructure/AuthExtensions.cs`:

```csharp
namespace Ticketing.Api.Infrastructure;

public static class AuthExtensions
{
    public static IServiceCollection AddTicketingAuth(this IServiceCollection services)
    {
        // Reads Authentication:Schemes:Bearer from configuration (dotnet user-jwts writes it)
        services.AddAuthentication().AddJwtBearer();
        services.AddAuthorization();

        return services;
    }
}
```

`AddJwtBearer()` with no arguments reads the `Authentication:Schemes:Bearer` section that `user-jwts` just wrote. Swap that section for your real identity provider's settings (Entra ID, Auth0, Okta) and this code doesn't change.

Register it in `Program.cs` after `AddTicketing()`:

```csharp
builder.Services.AddTicketingAuth();
```

And add the middleware after `UseStaticFiles()`. Order matters: authentication works out *who* you are, then authorization decides what you can do.

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

Now require a user on every ticket endpoint, once, at the versioned group in `TicketingEndpoints.cs`:

```csharp
        var api = app.NewVersionedApi("Ticketing")
            .MapGroup("/api/v{version:apiVersion}")
            .HasApiVersion(new ApiVersion(1, 0))
            .RequireAuthorization();   // every ticket endpoint needs a signed-in user
```

Health, the OpenAPI document, the lab client, and `/dev` are mapped outside this group, so they stay public.

### Give 401 and 403 a shape

The framework's 401 and 403 responses have empty bodies, so they go through the status code pages you added in Lab 2. Add their types to the catalog. In `Infrastructure/Problems.cs`, add two entries to `ProblemTypes`:

```csharp
    public const string Unauthorized = Base + "unauthorized";           // 401: no valid identity
    public const string Forbidden = Base + "forbidden";                 // 403: known identity, not allowed
```

And a factory to `Problems`, for when a handler forbids something itself:

```csharp
    public static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            type: ProblemTypes.Forbidden,
            title: "Forbidden",
            detail: detail);
```

Then map them in `ErrorHandlingExtensions.cs`, in the `problem.Status switch`:

```csharp
                        StatusCodes.Status400BadRequest => ProblemTypes.Validation,
                        StatusCodes.Status401Unauthorized => ProblemTypes.Unauthorized,
                        StatusCodes.Status403Forbidden => ProblemTypes.Forbidden,
```

### Checkpoint

**9 passing.** Send the first request in `03-auth.http` (no token): a 401 Problem with a `WWW-Authenticate: Bearer` header telling the caller how to authenticate. Every other request with a valid token works, for anyone, which is the next problem.

---

## Step 3: Roles and the agent policy (5 min)

A **policy** is a named rule. Declare it once, apply it anywhere. Replace `AuthExtensions.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Ticketing.Api.Infrastructure;

public static class Roles
{
    public const string Requester = "requester";
    public const string Agent = "agent";
    public const string Admin = "admin";
}

public static class Policies
{
    /// <summary>Agents and admins: assign, close, and see every ticket.</summary>
    public const string Agent = "Agent";
}

public static class AuthExtensions
{
    public static IServiceCollection AddTicketingAuth(this IServiceCollection services)
    {
        // Reads Authentication:Schemes:Bearer from configuration (dotnet user-jwts writes it)
        services.AddAuthentication().AddJwtBearer();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, p => p.RequireRole(Roles.Agent, Roles.Admin));

        return services;
    }

    public static bool IsAgent(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Agent) || user.IsInRole(Roles.Admin);

    /// <summary>The signed-in user's id (the token's unique_name / sub).</summary>
    public static string UserId(this ClaimsPrincipal user) => user.Identity?.Name ?? "";
}
```

`AddAuthorizationBuilder()` replaces `AddAuthorization()`. The two small extensions, `IsAgent()` and `UserId()`, keep the claims plumbing out of your handlers.

Apply the policy where the endpoint is mapped. In `AssignTicket.cs`:

```csharp
        group.MapPost("/{id:guid}/assign", Handle)
            .WithName("AssignTicket")
            .RequireAuthorization(Policies.Agent);
```

And the same in `CloseTicket.cs`, with `"CloseTicket"`.

### Checkpoint

**11 passing.** Alice can no longer close her own ticket. Notice you didn't write a 403 anywhere: the framework rejected the request before your handler ran, and Lab 2's status code pages gave it a body.

---

## Step 4: "Only your own tickets" (10 min)

A route policy can't express this rule. "May alice read ticket 1111?" depends on the **ticket**, and the ticket doesn't exist until the handler loads it. That's **resource-based authorization**: load the resource, then ask.

Create `src/Ticketing.Api/Features/Tickets/TicketAccess.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

/// <summary>"May this user touch this ticket?" The answer depends on the ticket, not just the user.</summary>
public class TicketAccessRequirement : IAuthorizationRequirement;

public class TicketAccessHandler : AuthorizationHandler<TicketAccessRequirement, Ticket>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TicketAccessRequirement requirement, Ticket ticket)
    {
        // Agents can touch any ticket; requesters only their own
        if (context.User.IsAgent() || ticket.RequesterId == context.User.UserId())
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
```

Register it as a policy. Here's the finished `AuthExtensions.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Infrastructure;

public static class Roles
{
    public const string Requester = "requester";
    public const string Agent = "agent";
    public const string Admin = "admin";
}

public static class Policies
{
    /// <summary>Agents and admins: assign, close, and see every ticket.</summary>
    public const string Agent = "Agent";

    /// <summary>Resource-based: may this user touch this particular ticket?</summary>
    public const string TicketAccess = "TicketAccess";
}

public static class AuthExtensions
{
    public static IServiceCollection AddTicketingAuth(this IServiceCollection services)
    {
        // Reads Authentication:Schemes:Bearer from configuration (dotnet user-jwts writes it)
        services.AddAuthentication().AddJwtBearer();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, p => p.RequireRole(Roles.Agent, Roles.Admin))
            .AddPolicy(Policies.TicketAccess, p => p.AddRequirements(new TicketAccessRequirement()));

        services.AddSingleton<IAuthorizationHandler, TicketAccessHandler>();

        return services;
    }

    public static bool IsAgent(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Agent) || user.IsInRole(Roles.Admin);

    /// <summary>The signed-in user's id (the token's unique_name / sub).</summary>
    public static string UserId(this ClaimsPrincipal user) => user.Identity?.Name ?? "";
}
```

Now ask the question in the handlers. `GetTicket.cs`:

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

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, ClaimsPrincipal user, IAuthorizationService auth,
        CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!(await auth.AuthorizeAsync(user, ticket, Policies.TicketAccess)).Succeeded)
            return Problems.Forbidden($"You don't have access to ticket {ticket.Id}.");

        return TypedResults.Ok(ticket.ToResponse());
    }
}
```

`ClaimsPrincipal user` and `IAuthorizationService auth` are both injected as handler parameters, the same way the store is.

Make the same change in `UpdateTicket.cs` and `Comments/ListComments.cs`: add the two parameters, and put the access check right after the not-found check. `Comments/AddComment.cs` didn't load the ticket before, so it needs a lookup first:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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

    public static async Task<Results<Created<CommentResponse>, ProblemHttpResult>> Handle(
        Guid ticketId, AddCommentRequest request, ITicketStore store, ClaimsPrincipal user,
        IAuthorizationService auth, HttpRequest http, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(ticketId, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(ticketId);

        if (!(await auth.AuthorizeAsync(user, ticket, Policies.TicketAccess)).Succeeded)
            return Problems.Forbidden($"You don't have access to ticket {ticket.Id}.");

        var comment = new Comment
        {
            AuthorId = request.AuthorId,
            Body = request.Body,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var added = await store.AddCommentAsync(ticketId, comment, cancellationToken);
        return added is null
            ? Problems.TicketNotFound(ticketId)
            : TypedResults.Created(http.LocationFor(added.Id), added.ToResponse());
    }
}
```

### Checkpoint

**14 passing.** Four left: two about where identity comes from, and two about callers that aren't people.

---

## Step 5: Identity comes from the token (5 min)

**Listing.** A requester asking for "all tickets" shouldn't get a 403. They should get *their* tickets. Filter, don't forbid. `ListTickets.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class ListTickets
{
    public static RouteGroupBuilder MapListTickets(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListTickets");
        return group;
    }

    public static async Task<Ok<List<TicketResponse>>> Handle(
        ITicketStore store, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var tickets = await store.ListAsync(cancellationToken);

        // Agents see everything; requesters see only their own tickets
        var visible = user.IsAgent()
            ? tickets
            : tickets.Where(t => t.RequesterId == user.UserId());

        return TypedResults.Ok(visible.Select(t => t.ToResponse()).ToList());
    }
}
```

**Creating.** The request body has a `requesterId`, and right now bob can put `"alice"` in it. The body says who the ticket is *for*; the token says who is *asking*. `CreateTicket.cs`:

```csharp
using System.Security.Claims;
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

    public static async Task<Results<Created<TicketResponse>, ProblemHttpResult>> Handle(
        CreateTicketRequest request, ITicketStore store, ClaimsPrincipal user, HttpRequest http,
        CancellationToken cancellationToken)
    {
        // Requesters open tickets for themselves; agents can open them on anyone's behalf
        if (!user.IsAgent() && request.RequesterId != user.UserId())
            return Problems.Forbidden("You can only create tickets for yourself.");

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

### Checkpoint

**16 passing.** The last 2 are API keys.

---

## Step 6: API keys for service callers (10 min)

A monitoring dashboard or a nightly integration isn't a person, and can't sign in to get a token. The usual answer is an API key: a long random secret, sent in a header, mapped to a named client with specific permissions.

Create `src/Ticketing.Api/Infrastructure/ApiKeyAuthentication.cs`:

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ticketing.Api.Infrastructure;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";
}

/// <summary>One service caller, from configuration: ApiKeys:{name}:Key and ApiKeys:{name}:Roles.</summary>
public class ApiKeyClient
{
    public string Key { get; set; } = "";
    public string[] Roles { get; set; } = [];
}

/// <summary>
/// Authenticates service callers (dashboards, integrations, scripts) that send X-Api-Key.
/// The caller becomes a ClaimsPrincipal like any other, so every policy applies unchanged.
/// </summary>
public class ApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var supplied = Request.Headers[ApiKeyDefaults.Header].ToString();
        if (string.IsNullOrEmpty(supplied))
            return Task.FromResult(AuthenticateResult.NoResult());

        var clients = configuration.GetSection("ApiKeys").Get<Dictionary<string, ApiKeyClient>>() ?? [];
        foreach (var (name, client) in clients)
        {
            if (!KeysMatch(client.Key, supplied))
                continue;

            var claims = new List<Claim> { new(ClaimTypes.Name, name) };
            claims.AddRange(client.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }

    /// <summary>
    /// Compare hashes in constant time, so response timing reveals nothing about how much of a
    /// guessed key was right (and nothing about its length).
    /// </summary>
    private static bool KeysMatch(string expected, string supplied) =>
        expected.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}
```

The handler turns a key into a `ClaimsPrincipal` with a name and roles, exactly like a JWT does. That's the point: everything downstream (the `Agent` policy, `TicketAccess`, and Lab 4's rate limiter) works for key callers without knowing they exist.

Now register it alongside JWT bearer. The trick is a **policy scheme**: it isn't a real scheme, it just looks at each request and forwards to the right one. Here's the finished `AuthExtensions.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Infrastructure;

public static class Roles
{
    public const string Requester = "requester";
    public const string Agent = "agent";
    public const string Admin = "admin";
}

public static class Policies
{
    /// <summary>Agents and admins: assign, close, and see every ticket.</summary>
    public const string Agent = "Agent";

    /// <summary>Resource-based: may this user touch this particular ticket?</summary>
    public const string TicketAccess = "TicketAccess";
}

public static class AuthExtensions
{
    private const string BearerOrApiKey = "BearerOrApiKey";

    public static IServiceCollection AddTicketingAuth(this IServiceCollection services)
    {
        services.AddAuthentication(BearerOrApiKey)
            // Reads Authentication:Schemes:Bearer from configuration (dotnet user-jwts writes it)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(ApiKeyDefaults.Scheme, null)
            // Not a real scheme: picks one per request, so every policy accepts either
            .AddPolicyScheme(BearerOrApiKey, "Bearer token or API key", options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ApiKeyDefaults.Header)
                        ? ApiKeyDefaults.Scheme
                        : JwtBearerDefaults.AuthenticationScheme);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, p => p.RequireRole(Roles.Agent, Roles.Admin))
            .AddPolicy(Policies.TicketAccess, p => p.AddRequirements(new TicketAccessRequirement()));

        services.AddSingleton<IAuthorizationHandler, TicketAccessHandler>();

        return services;
    }

    public static bool IsAgent(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Agent) || user.IsInRole(Roles.Admin);

    /// <summary>The signed-in user's id (the token's unique_name / sub).</summary>
    public static string UserId(this ClaimsPrincipal user) => user.Identity?.Name ?? "";
}
```

Add a key to `appsettings.Development.json`:

```json
  "ApiKeys": {
    "ops-dashboard": {
      "Key": "dev-ops-dashboard-key-change-me",
      "Roles": [ "agent" ]
    }
  }
```

### Checkpoint

**All 18 passing.** Send the 2 API key requests at the bottom of `03-auth.http`: the key gets in, a guessed key gets a 401 Problem.

> **A testing gotcha you'll hit eventually.** The API key tests use `factory.WithWebHostBuilder(...)` to add a test key. That starts a **separate copy of the app**, with its own in-memory store. A ticket created through the main factory doesn't exist there, which is why the test closes a seeded ticket instead.

---

## Finished

Your `Program.cs`:

```csharp
using Ticketing.Api.Chaos;
using Ticketing.Api.Features;
using Ticketing.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiDefaults();
builder.Services.AddErrorHandling();
builder.Services.AddTicketing();
builder.Services.AddTicketingAuth();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseErrorHandling();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().WithDocumentPerVersion();
app.MapTicketingEndpoints();
app.MapHealthChecks("/health");
app.MapChaosEndpoints();

app.Run();

public partial class Program;
```

```
dotnet test
```

**All green.** Run the API and work through `http/03-auth.http`. Then switch users in the lab client and watch the ticket list change.

---

## Talk it over

### 403 or 404?

When bob asks for alice's ticket, he gets a 403. That tells bob the ticket **exists**. For ticket IDs that are random GUIDs, that's usually fine. For anything guessable (sequential IDs, email addresses, usernames), returning 404 instead hides whether the thing exists at all. GitHub does exactly this for private repositories. Pick deliberately, and pick the same answer everywhere.

### Never trust identity in the body

The `requesterId` field lets a caller *claim* to be someone. You fixed it with a rule, but the cleaner design is not to accept it from requesters at all: take it from the token, and only let agents name someone else. Removing the field from the request would be a breaking change (Module 1B), which is exactly the kind of thing that waits for v2.

### Roles, scopes, and claims

Roles answer "what kind of user is this?" Real identity providers also issue **scopes** (what this *app* may do on the user's behalf) and arbitrary **claims** (department, tenant, subscription tier). Policies can require any of them, so the same `RequireAuthorization(Policies.Agent)` keeps working when "agent" becomes a claim from Entra ID instead of a role from `user-jwts`.

### API keys or tokens?

A key identifies a client. It can't express scopes, expiry, or acting on a user's behalf, and it's valid until someone revokes it. Keys are fine for a dashboard or a nightly job; for anything bigger, the OAuth 2.0 client credentials flow gives a service a short-lived token, and the JWT code from this lab already validates it. Keeping keys safe in production (hashing, rotation, scoping) is in Lab 4's talk-it-over.

### `user-jwts` is for development

It's a fast way to test auth locally, nothing more. In production, tokens come from an identity provider, and your API only *validates* them. Because `AddJwtBearer()` reads its settings from configuration, moving to a real provider is a configuration change, not a code change.
