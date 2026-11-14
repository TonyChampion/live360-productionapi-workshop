# Lab 2: Every Failure Has a Shape

**Module 2: Validation and Error Contracts** | about 45 to 60 minutes

The demo version of an API handles the happy path. A production API designs its failures. In this lab you'll make every failure leave through a designed exit: the right status code, a Problem Details body with a stable type a caller can code against, and a trace ID they can hand to support.

**You're done when** every error the API can produce is `application/problem+json`, and `dotnet test` is fully green.

| Folder | Use it for |
|---|---|
| `Start/` | Where you work |
| `End/` | The finished lab. Compare against it if you get stuck |

---

## Before you start

Open `labs/Lab02/Start` and run the tests:

```
dotnet test --filter "Lab=2"
```

You should see **17 failures and 3 passes**. One pass is `TicketLifecycleTests`, the happy path. The other two are guards in `BusinessRuleTests`: an urgent ticket with a real description, and a low-priority ticket with none, are both created. All three must stay green all lab.

### What's new in your Start folder

Three endpoints have been added for you, written the way they often are in a first version:

| Endpoint | File |
|---|---|
| `PATCH /api/v1/tickets/{id}` | `Features/Tickets/UpdateTicket.cs` |
| `POST /api/v1/tickets/{id}/assign` | `Features/Tickets/AssignTicket.cs` |
| `POST /api/v1/tickets/{id}/close` | `Features/Tickets/CloseTicket.cs` |

`Domain/TicketTransitions.cs` holds the rules for which status changes are legal. The endpoints check those rules, then **throw** when a change isn't allowed. Nothing validates input.

### See the problem first (3 min)

Run the API (`dotnet run --project src/Ticketing.Api`) and send the requests in `http/02-errors.http`. Note what comes back today:

- A ticket with no title and no requester: **201 Created**
- A ticket that doesn't exist: **404 with an empty body**
- Closing a ticket twice: **500**, from an `InvalidOperationException`
- `/dev/boom`: the **developer exception page**, stack trace and all

That last one is worth a moment. In Development, ASP.NET Core shows you everything. Ship an app with `ASPNETCORE_ENVIRONMENT=Development` by mistake and your callers see it too.

---

## Who caused the failure?

Every error status answers that question for the caller:

| Status | Meaning | Who should fix it |
|---|---|---|
| **400** Bad Request | The request is malformed or invalid | The caller, by changing the request |
| **401 / 403** | Not authenticated / not allowed | Coming in Lab 3 |
| **404** Not Found | The thing doesn't exist | The caller, by asking for something else |
| **409** Conflict | The request is fine, but the resource's current state won't allow it | Nobody's "wrong"; the caller re-reads state and decides |
| **500** Internal Server Error | Our bug | Us |

The distinction between 400 and 409 carries the rest of the day. "Close" with a valid ticket ID is a perfectly good request. It's the ticket being already closed that makes it fail.

---

## Step 1: Problem Details everywhere (10 min)

[RFC 9457 Problem Details](https://www.rfc-editor.org/rfc/rfc9457) is the standard JSON shape for HTTP API errors. Its `type` field is a URI that identifies the kind of problem, and it's the part callers should code against. Titles and details are for humans and may change.

Start with a catalog of every problem type this API returns. Create `src/Ticketing.Api/Infrastructure/Problems.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// The catalog of problem types this API can return. Each type is a stable identifier
/// a caller can code against; title and detail are for humans.
/// </summary>
public static class ProblemTypes
{
    public const string Base = "https://ticketing.example/problems/";

    public const string Validation = Base + "validation";               // 400: the request is malformed
    public const string TicketNotFound = Base + "ticket-not-found";     // 404: no such ticket
    public const string InvalidTransition = Base + "invalid-transition"; // 409: ticket's state won't allow it
    public const string NotFound = Base + "not-found";                  // 404: no such route
    public const string Unexpected = Base + "unexpected";               // 500: our bug, not theirs
}

/// <summary>
/// Factory methods for the problems a handler returns on purpose.
/// </summary>
public static class Problems
{
    public static ProblemHttpResult TicketNotFound(Guid id) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            type: ProblemTypes.TicketNotFound,
            title: "Ticket not found",
            detail: $"No ticket with id {id} exists.");

    public static ProblemHttpResult InvalidTransition(Ticket ticket, string action) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: ProblemTypes.InvalidTransition,
            title: "Invalid status transition",
            detail: $"Ticket {ticket.Id} is {ticket.Status} and cannot be {action}.",
            extensions: new Dictionary<string, object?> { ["currentStatus"] = ticket.Status.ToString() });
}
```

Now turn on Problem Details for the whole app. Create `src/Ticketing.Api/Infrastructure/ErrorHandlingExtensions.cs`:

```csharp
using System.Diagnostics;

namespace Ticketing.Api.Infrastructure;

public static class ErrorHandlingExtensions
{
    /// <summary>
    /// Problem Details for every failure: designed ones, validation, routing, and unhandled exceptions.
    /// </summary>
    public static IServiceCollection AddErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var problem = context.ProblemDetails;
                var http = context.HttpContext;

                // Framework-generated problems point at the RFC; give them our catalog types instead
                if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org/"))
                {
                    problem.Type = problem.Status switch
                    {
                        StatusCodes.Status400BadRequest => ProblemTypes.Validation,
                        StatusCodes.Status404NotFound => ProblemTypes.NotFound,
                        _ => ProblemTypes.Unexpected
                    };
                }

                problem.Instance ??= $"{http.Request.Method} {http.Request.Path}";
                problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
            };
        });

        return services;
    }

    public static WebApplication UseErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler();   // unhandled exceptions -> Problem Details
        app.UseStatusCodePages();    // empty error responses (unknown route, etc.) -> Problem Details
        return app;
    }
}
```

Three things are happening:

- **`AddProblemDetails`** gives the framework a single place to write errors, and `CustomizeProblemDetails` runs on every one of them. That's where every error gets a catalog type and a `traceId`.
- **`UseExceptionHandler`** catches unhandled exceptions and writes them as Problem Details instead of the developer page.
- **`UseStatusCodePages`** gives a body to responses that would otherwise be empty, like a 404 for a route that doesn't exist.

In `Program.cs`, register it with the other services:

```csharp
builder.Services.AddErrorHandling();
```

And put the middleware first, right after `var app = builder.Build();`. It has to wrap everything that could fail:

```csharp
app.UseErrorHandling();
```

### Checkpoint

```
dotnet test --filter "Lab=2"
```

**8 passing.** Send `/dev/boom` again: no stack trace, just a type, a status, and a `traceId`. The trace ID is the same one in your logs, so a caller can quote it and you can find the exact request.

---

## Step 2: A safety net you control (5 min)

The built-in handler already hides internals. What it doesn't do is log the failure your way, or recognize when an exception was really the caller's fault.

Create `src/Ticketing.Api/Infrastructure/GlobalExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// The safety net. Anything a handler didn't turn into a designed result lands here,
/// gets logged with full detail, and leaves as a Problem Details body with no internals.
/// </summary>
public class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // The request couldn't be read (bad JSON, unknown enum value): the caller's mistake
        if (exception is BadHttpRequestException badRequest)
        {
            context.Response.StatusCode = badRequest.StatusCode;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Type = ProblemTypes.Validation,
                    Title = "The request could not be read",
                    Detail = "Check that the body is valid JSON and every value has the expected type."
                }
            });
        }

        // Everything else is our bug. Log the details; tell the caller only what helps them.
        logger.LogError(exception, "Unhandled exception for {Method} {Path}",
            context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Type = ProblemTypes.Unexpected,
                Title = "An unexpected error occurred",
                Detail = "The error has been logged. Quote the traceId if you contact support."
            }
        });
    }
}
```

Register it in `AddErrorHandling()`, just before `return services;`:

```csharp
        services.AddExceptionHandler<GlobalExceptionHandler>();
```

`BadHttpRequestException` is what ASP.NET Core throws when it can't read the request at all, for example a `priority` of `"Critical"` that isn't in the enum. That's a 400, not our bug.

### Checkpoint

**9 passing.** The unknown priority now returns 400.

> **The handler should rarely fire.** It's the net under the trapeze, not the act. Everything a handler *expects* to go wrong (a missing ticket, an invalid transition, bad input) should be a return value, not an exception. That's the next three steps.

---

## Step 3: 404 means "that ticket doesn't exist" (5 min)

A bare 404 can't tell a caller whether the ticket is missing or the URL is wrong. The catalog gives them `ticket-not-found` for one and `not-found` for the other.

Update `GetTicket.cs` to return the catalog problem:

```csharp
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
        Guid id, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        return ticket is null
            ? Problems.TicketNotFound(id)
            : TypedResults.Ok(ticket.ToResponse());
    }
}
```

Two changes: the return type swaps `NotFound` for `ProblemHttpResult`, and `TypedResults.NotFound()` becomes `Problems.TicketNotFound(id)`.

Make the same two changes in:

- `UpdateTicket.cs`, `AssignTicket.cs`, and `CloseTicket.cs` (use `id`)
- `Comments/ListComments.cs` and `Comments/AddComment.cs` (use `ticketId`)

Add `using Ticketing.Api.Infrastructure;` wherever it's missing.

### Checkpoint

**11 passing.**

---

## Step 4: 409 means "not in this state" (5 min)

Here's the demo-grade code you inherited in `CloseTicket.cs`:

```csharp
        if (!ticket.CanMoveTo(TicketStatus.Closed))
            throw new InvalidOperationException($"Ticket {id} is already {ticket.Status}.");
```

Closing a closed ticket isn't a bug in the API. It's a predictable outcome, so it should be a designed result. Replace the throw:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

public static class CloseTicket
{
    public static RouteGroupBuilder MapCloseTicket(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/close", Handle).WithName("CloseTicket");
        return group;
    }

    public static async Task<Results<Ok<TicketResponse>, ProblemHttpResult>> Handle(
        Guid id, ITicketStore store, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        if (ticket is null)
            return Problems.TicketNotFound(id);

        if (!ticket.CanMoveTo(TicketStatus.Closed))
            return Problems.InvalidTransition(ticket, "closed");

        ticket.Status = TicketStatus.Closed;
        await store.UpdateAsync(ticket, cancellationToken);
        return TypedResults.Ok(ticket.ToResponse());
    }
}
```

In `AssignTicket.cs`, replace its `throw` the same way:

```csharp
        if (!ticket.CanBeAssigned())
            return Problems.InvalidTransition(ticket, "assigned");
```

`InvalidTransition` adds a `currentStatus` extension to the body. Extensions are how a problem carries data a caller can act on. Here, it tells them the ticket's state without another request.

### Checkpoint

**13 passing.** Every remaining failure is about validation.

---

## Step 5: Validate on the way in (10 min)

.NET 10 has validation built into minimal APIs. You describe the rules on the request contracts, and the framework checks them before your handler runs, returning a 400 Problem Details when they fail.

Turn it on in `AddErrorHandling()`, after the exception handler:

```csharp
        services.AddValidation();
```

Then add rules to the contracts. `Features/Tickets/TicketContracts.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Tickets;

// What a caller may send. No Id, Status, or AssigneeId: the server owns those.
public record CreateTicketRequest(
    [Required, StringLength(200)] string Title,
    [StringLength(4000)] string Description,
    TicketPriority Priority,
    [Required, StringLength(100)] string RequesterId);

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

`Features/Comments/CommentContracts.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Features.Comments;

public record AddCommentRequest(
    [Required, StringLength(100)] string AuthorId,
    [Required, StringLength(4000)] string Body);

public record CommentResponse(Guid Id, Guid TicketId, string AuthorId, string Body, DateTimeOffset CreatedAt);

public static class CommentMappings
{
    public static CommentResponse ToResponse(this Comment c) =>
        new(c.Id, c.TicketId, c.AuthorId, c.Body, c.CreatedAt);
}
```

In `UpdateTicket.cs`, every field is optional, but a title that *is* sent can't be empty:

```csharp
public record UpdateTicketRequest(
    [MinLength(1), StringLength(200)] string? Title,
    [StringLength(4000)] string? Description,
    Domain.TicketPriority? Priority);
```

In `AssignTicket.cs`:

```csharp
public record AssignTicketRequest([Required, StringLength(100)] string AssigneeId);
```

Both files need `using System.ComponentModel.DataAnnotations;`.

### Checkpoint: still red?

Run the tests. **The same 6 validation tests still fail.** Validation is working, so read the failure message and look at a response in `02-errors.http`:

```json
"errors": {
  "Title": ["The Title field is required."],
  "RequesterId": ["The RequesterId field is required."]
}
```

The caller sent `title` and `requesterId`. The errors come back as `Title` and `RequesterId`. A client mapping errors onto its form fields would find nothing. That's a contract bug, and the tests caught it.

### Fix the error keys

In `CustomizeProblemDetails`, rename validation error keys to match the JSON naming. Here's the finished `ErrorHandlingExtensions.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace Ticketing.Api.Infrastructure;

public static class ErrorHandlingExtensions
{
    /// <summary>
    /// Problem Details for every failure: designed ones, validation, routing, and unhandled exceptions.
    /// </summary>
    public static IServiceCollection AddErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var problem = context.ProblemDetails;
                var http = context.HttpContext;

                // Framework-generated problems point at the RFC; give them our catalog types instead
                if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org/"))
                {
                    problem.Type = problem.Status switch
                    {
                        StatusCodes.Status400BadRequest => ProblemTypes.Validation,
                        StatusCodes.Status404NotFound => ProblemTypes.NotFound,
                        _ => ProblemTypes.Unexpected
                    };
                }

                // Validation error keys should match the JSON the caller sent (title, not Title)
                if (problem is HttpValidationProblemDetails validation)
                {
                    var renamed = validation.Errors.ToDictionary(
                        e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value);
                    validation.Errors.Clear();
                    foreach (var (key, messages) in renamed)
                        validation.Errors[key] = messages;
                }

                problem.Instance ??= $"{http.Request.Method} {http.Request.Path}";
                problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
            };
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddValidation();

        return services;
    }

    public static WebApplication UseErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler();   // unhandled exceptions -> GlobalExceptionHandler
        app.UseStatusCodePages();    // empty error responses (unknown route, etc.) -> Problem Details
        return app;
    }
}
```

### Checkpoint

**19 passing.** One left: an urgent ticket with a description like "Help!" still gets a 201. No attribute can say "at least 10 characters, but only when the priority is Urgent." That's Step 6.

---

## Step 6: Rules attributes can't express (10 min)

Attributes describe the **shape** of a request: required, how long, which values. Some rules are about **meaning**, and one field depends on another. Support's rule: an **urgent** ticket needs a description of at least 10 characters, so whoever picks it up at 2 a.m. knows what's on fire. A low-priority ticket with no description is fine.

`[StringLength]` can't say "only when the priority is Urgent." That's what [FluentValidation](https://docs.fluentvalidation.net/) is for. Built-in validation keeps doing the shape checks; FluentValidation adds the rules, and both come back in the same 400 Problem Details shape.

Run the tests: `Urgent_ticket_without_a_real_description_is_a_validation_problem` fails. Send the new urgent-ticket request in `02-errors.http`: today it's a 201.

### Add the package

In `Directory.Packages.props`, with the other API packages:

```xml
<PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.2.0" />
```

In `src/Ticketing.Api/Ticketing.Api.csproj`:

```xml
<PackageReference Include="FluentValidation.DependencyInjectionExtensions" />
```

### Write the rule in the slice

The rule belongs to creating a ticket, so it lives in `CreateTicket.cs`, under the `CreateTicket` class (add `using FluentValidation;` at the top):

```csharp
/// <summary>
/// Rules the attributes on <see cref="CreateTicketRequest"/> can't express. Here, one field depends on another.
/// </summary>
public class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(r => r.Description)
            .MinimumLength(10)
            .When(r => r.Priority == TicketPriority.Urgent)
            .WithMessage("Urgent tickets need a description of at least 10 characters.");
    }
}
```

### Run it before the handler

Minimal APIs don't know about FluentValidation, so an **endpoint filter** runs the validator. Create `Infrastructure/ValidationFilter.cs`:

```csharp
using FluentValidation;
using System.Text.Json;

namespace Ticketing.Api.Infrastructure;

public class ValidationFilter<TRequest> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        // Resolved per request: validators are scoped, so they can't be captured at startup
        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>();
        if (request is null || validator is null)
            return await next(context);

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid)
            return await next(context);

        // Same shape as built-in validation: a 400 ValidationProblem, keyed the way the caller sent the JSON
        var errors = result.Errors
            .GroupBy(e => JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        return TypedResults.ValidationProblem(errors);
    }
}

public static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder WithValidator<TRequest>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>()
               .ProducesValidationProblem();
}
```

`TypedResults.ValidationProblem` goes through the same Problem Details pipeline as everything else, so `CustomizeProblemDetails` gives it the catalog type and a `traceId`. Callers can't tell which validator caught them.

### Wire it up

Register every validator in the assembly, in `AddErrorHandling` next to `AddValidation()` (add `using FluentValidation;`):

```csharp
services.AddValidation();
services.AddValidatorsFromAssemblyContaining<Program>();   // FluentValidation, for rules attributes can't express
```

And opt the endpoint in, in `CreateTicket.cs`:

```csharp
group.MapPost("/", Handle)
    .WithName("CreateTicket")
    .WithValidator<CreateTicketRequest>();
```

### Checkpoint

**All green.** Send the urgent-ticket request again: a 400 with `errors.description`, in exactly the same shape as the built-in validation errors.

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
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseErrorHandling();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi().WithDocumentPerVersion();
app.MapTicketingEndpoints();
app.MapHealthChecks("/health");
app.MapChaosEndpoints();

app.Run();

public partial class Program;
```

Run everything:

```
dotnet test
```

**All green.** Run the API and send every request in `http/02-errors.http` again. Compare each one with what you saw at the start.

---

## Talk it over

### Built-in validation or FluentValidation?

The built-in validation you used keeps rules next to the contract and needs no package. [FluentValidation](https://docs.fluentvalidation.net/) keeps rules in separate classes, which helps when rules get complex: conditional rules, rules that depend on other fields, or rules that call a service ("does this assignee exist?"). You used both: attributes for shape, FluentValidation for rules about meaning. That split scales well. Keep attributes for what a schema can say, and move a rule to a validator the moment it depends on another field or needs a service. A rule that needs the database ("does this assignee exist?") can go in an async validator or in the handler; either way, return the same Problem Details shape.

### What counts as a breaking change?

You just changed this API's behavior. Callers that got a 500 now get a 409, and empty 404s now have bodies. Is that breaking?

**Usually breaking**
- Removing or renaming a field, in a request or a response
- Changing a field's type or meaning
- Adding a required request field
- Tightening validation so previously accepted requests now fail
- Changing which status code or problem `type` a known failure returns

**Usually safe**
- Adding optional request fields or new response fields
- Adding endpoints
- Adding new problem types for failures that didn't have one

By that list, the 500 to 409 change is safe in practice (nobody codes against a 500), but the new validation is technically breaking: a request with an empty title used to succeed. On a public API, that's a judgment call to make deliberately, not discover after a caller complains.

### Signaling deprecation

When a version has to go, tell callers early and in the response itself:

- **Asp.Versioning** can mark a version deprecated (`HasDeprecatedApiVersion`), which adds it to an `api-deprecated-versions` header
- The **`Deprecation`** header ([RFC 9745](https://www.rfc-editor.org/rfc/rfc9745)) says the resource is deprecated
- The **`Sunset`** header ([RFC 8594](https://www.rfc-editor.org/rfc/rfc8594)) says when it stops working

This is why versioning went in on day one: when v2 arrives, v1 can be deprecated on a schedule instead of broken on a Tuesday.
