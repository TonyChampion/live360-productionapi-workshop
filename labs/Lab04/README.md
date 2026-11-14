# Lab 4: Abuse Surfaces

**Module 4: Protecting the API** | about 45 to 60 minutes

Authentication tells you who's calling. It doesn't stop a signed-in user, or a service with an API key, from creating ten thousand tickets in a minute, and it says nothing about what a browser should do with your responses. In this lab you'll close those gaps: rate limiting, security headers, and CORS. The talk-it-over section covers keeping Lab 3's API keys safe in production.

**You're done when** creates are rate limited per user, every response carries security headers, only the support site's browser origin may call the API, and `dotnet test` is fully green.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/Lab04/Start` and run this lab's tests:

```
dotnet test --filter "Lab=4"
```

**5 failures, 3 passing.** The 3 passing are guards: another user isn't blocked by someone else's limit, reads aren't rate limited, and an unknown origin already gets no CORS permission. They must stay green.

### What's new in your Start folder

- **The lab client has a "Trip the rate limiter" run case.** Sign in (Lab 3), click it, and watch the request log.
- **`http/04-protection.http`** covers every part of this lab.

---

## Step 1: Rate limit ticket creation (10 min)

Creating a ticket is the abuse surface here: it writes data, it notifies people, and a script can do it in a loop. Reading is cheap, so leave reads alone.

Add a problem type to `ProblemTypes` in `Infrastructure/Problems.cs`:

```csharp
    public const string RateLimited = Base + "rate-limited";            // 429: slow down
```

Create `src/Ticketing.Api/Infrastructure/RateLimitingExtensions.cs`:

```csharp
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ticketing.Api.Infrastructure;

public static class RateLimitPolicies
{
    public const string CreateTicket = "create-ticket";
}

/// <summary>Bound from RateLimits:CreateTicket, so limits change without a redeploy.</summary>
public class CreateTicketLimits
{
    public int PermitLimit { get; set; } = 20;
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddTicketingRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CreateTicketLimits>(configuration.GetSection("RateLimits:CreateTicket"));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // One fixed-window bucket per signed-in user (or per IP for anonymous callers)
            options.AddPolicy(RateLimitPolicies.CreateTicket, http =>
            {
                var limits = http.RequestServices.GetRequiredService<IOptions<CreateTicketLimits>>().Value;
                var partitionKey = http.User.Identity?.IsAuthenticated == true
                    ? $"user:{http.User.UserId()}"
                    : $"ip:{http.Connection.RemoteIpAddress}";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0
                });
            });

            // A rejection is a designed failure too: tell the caller when to come back
            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Type = ProblemTypes.RateLimited,
                        Title = "Too many requests",
                        Detail = "You've hit the limit for this action. Try again after the Retry-After interval."
                    }
                });
            };
        });

        return services;
    }
}
```

A few decisions in there worth naming:

- **Partition by user, not globally.** One noisy user shouldn't lock everyone out. Anonymous callers fall back to their IP address.
- **`QueueLimit = 0`.** Reject immediately instead of holding the request open. A queued request ties up a connection and still fails later.
- **`OnRejected` writes a Problem**, with a `Retry-After` header saying when the window resets. A 429 is a designed failure like any other.

Put the limits in `appsettings.json`, so they can change without a redeploy:

```json
  "RateLimits": {
    "CreateTicket": {
      "PermitLimit": 20,
      "WindowSeconds": 60
    }
  }
```

Apply the policy to the one endpoint that needs it. In `CreateTicket.cs`:

```csharp
        group.MapPost("/", Handle)
            .WithName("CreateTicket")
            .RequireRateLimiting(RateLimitPolicies.CreateTicket);
```

In `Program.cs`, register it after `AddTicketingAuth()`:

```csharp
builder.Services.AddTicketingRateLimits(builder.Configuration);
```

And add the middleware **after** `UseAuthorization()`. The limiter partitions by user, so it has to run after authentication has worked out who the user is:

```csharp
app.UseRateLimiter();
```

### Checkpoint

**4 passing.** In the lab client, sign in as alice and click **Trip the rate limiter**: 20 creates, then a 429 with `Retry-After`.

---

## Step 2: Security headers (5 min)

Headers tell a browser how to treat your responses. An API serves data, never pages, so it can be strict.

Create `src/Ticketing.Api/Infrastructure/SecurityHeadersExtensions.cs`:

```csharp
namespace Ticketing.Api.Infrastructure;

public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Headers that tell browsers how to treat our responses. Set in OnStarting so they're
    /// applied to every response, including errors written after the pipeline has failed.
    /// </summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";   // don't guess content types
                headers["Referrer-Policy"] = "no-referrer";

                // API responses are data, never pages: nothing may load, nothing may frame them.
                // (The lab client is a page with its own scripts, so it doesn't get this policy.)
                if (context.Request.Path.StartsWithSegments("/api"))
                    headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

                return Task.CompletedTask;
            });

            await next();
        });

        return app;
    }
}
```

- **`X-Content-Type-Options: nosniff`** stops a browser from guessing that your JSON is HTML and running it.
- **`Referrer-Policy: no-referrer`** keeps URLs (which may contain IDs) from leaking to other sites.
- **`Content-Security-Policy`** on `/api` says nothing may load and nothing may frame the response. The lab client is a real page with its own scripts and styles, so it doesn't get this policy. That's the distinction: lock down what you serve, based on what it is.

In `Program.cs`, make it the **first** middleware, right after `var app = builder.Build();`, so every response gets the headers:

```csharp
app.UseSecurityHeaders();
```

While you're there, stop advertising the server software. Before `builder.Build()`:

```csharp
// Don't advertise the server software in every response
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
```

The tests can't check that one: they run on an in-memory test server that never sends a `Server` header. Check it against the real thing:

```
dotnet run --project src/Ticketing.Api
curl -I http://localhost:5001/health
```

### Checkpoint

**7 passing.**

---

## Step 3: CORS for the support site (5 min)

Imagine the company's support site at `https://support.ticketing.example` calls this API from the browser. By default, browsers block that: a page can only call its own origin unless the API says otherwise. **CORS** is how the API says otherwise.

Create `src/Ticketing.Api/Infrastructure/CorsExtensions.cs`. The allowed headers include `X-Api-Key` from Lab 3, so a browser app can send a key too:

```csharp
namespace Ticketing.Api.Infrastructure;

public static class CorsPolicies
{
    public const string SupportSite = "SupportSite";
}

public static class CorsExtensions
{
    /// <summary>
    /// Which browser origins may call the API, from Cors:AllowedOrigins. CORS only affects browsers;
    /// it is not access control (curl and servers ignore it). Authentication still applies.
    /// </summary>
    public static IServiceCollection AddSupportSiteCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(CorsPolicies.SupportSite, policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PATCH")
            .WithHeaders("Authorization", "Content-Type", ApiKeyDefaults.Header)));

        return services;
    }
}
```

The allowed origin goes in `appsettings.json`:

```json
  "Cors": {
    "AllowedOrigins": [
      "https://support.ticketing.example"
    ]
  }
```

In `Program.cs`, register it:

```csharp
builder.Services.AddSupportSiteCors(builder.Configuration);
```

And add the middleware **before** `UseAuthentication()`. Before a cross-origin request, a browser sends a "preflight" `OPTIONS` request with no token, asking permission. CORS has to answer it before authentication rejects it:

```csharp
app.UseCors(CorsPolicies.SupportSite);
```

### Checkpoint

**All 8 passing.**

---

## Finished

Your `Program.cs`. Middleware order now matters a lot, so read it top to bottom:

```csharp
using Ticketing.Api.Chaos;
using Ticketing.Api.Features;
using Ticketing.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiDefaults();
builder.Services.AddErrorHandling();
builder.Services.AddTicketing();
builder.Services.AddTicketingAuth();
builder.Services.AddTicketingRateLimits(builder.Configuration);
builder.Services.AddSupportSiteCors(builder.Configuration);
builder.Services.AddHealthChecks();

// Don't advertise the server software in every response
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

app.UseSecurityHeaders();
app.UseErrorHandling();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(CorsPolicies.SupportSite);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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

**All green.** Work through `http/04-protection.http`, and look at the response headers on each request.

---

## Talk it over

### Rate limits across instances

This limiter keeps its counters in memory, per instance. Run three instances behind a load balancer and each user gets three times the limit. For a real fleet, enforce limits where all traffic passes: an API gateway (Azure API Management, for example) or a limiter backed by a shared store like Redis. The in-app limiter still earns its place as a second line of defense.

Fixed window is the simplest algorithm. ASP.NET Core also ships **sliding window** (smoother at the window edges), **token bucket** (allows short bursts), and **concurrency** (limits requests in flight, not per second). Pick by what you're protecting.

### API keys in production

Lab 3 gave service callers a key. A key is a password for a program, so treat it like one:

- **Store hashes, not keys.** If your config leaks, hashed keys are useless to an attacker. The constant-time comparison here already hashes both sides.
- **Allow more than one key per client**, so you can rotate: issue the new key, let the client switch, then revoke the old one.
- **Never accept keys in the query string.** URLs end up in logs, browser history, and referrer headers.
- **Know when to graduate.** API keys identify a client but can't express scopes, expiry, or delegation. For anything serious, the OAuth 2.0 client credentials flow gives a service a short-lived token from your identity provider, and Lab 3's code already validates it.

### CORS is not security

CORS only tells **browsers** whether a page on another site may read your responses. `curl`, Postman, and other servers ignore it entirely. It protects your users' browsers from being used against your API, but it is not access control. Authentication and authorization still do that job.

### Headers worth knowing about

- **`Strict-Transport-Security` (HSTS)** tells browsers to always use HTTPS. Add it in production with `app.UseHsts()` once you're sure HTTPS works everywhere.
- **`X-Frame-Options: DENY`** is the older form of `frame-ancestors 'none'`. Some teams send both for old browsers.
