using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Ticketing.Api.Chaos;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// Dependency failures are expected in production, not bugs: they get their own status codes,
/// a Retry-After where it helps, and a Warning in the log instead of an Error.
/// Registered ahead of GlobalExceptionHandler; anything it doesn't recognize falls through.
/// </summary>
public class DependencyExceptionHandler(
    IProblemDetailsService problemDetails,
    IOptions<ResilienceOptions> budget,
    ILogger<DependencyExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, type, title, detail, retryAfter) = exception switch
        {
            BrokenCircuitException => (503, ProblemTypes.StoreUnavailable, "The ticket store is unavailable",
                "The store has been failing, so calls are paused to let it recover. Try again after the Retry-After interval.",
                (TimeSpan?)TimeSpan.FromSeconds(budget.Value.BreakDurationSeconds)),
            StoreUnavailableException or TransientStoreException => (503, ProblemTypes.StoreUnavailable,
                "The ticket store is unavailable", "The store couldn't complete the request. Try again shortly.", null),
            TimeoutRejectedException => (504, ProblemTypes.Timeout, "The ticket store didn't respond in time",
                "The request ran out of time waiting for the store. Try again shortly.", null),
            _ => (0, "", "", "", null)
        };

        if (status == 0)
            return false;   // not a dependency failure: let GlobalExceptionHandler deal with it

        logger.LogWarning("{Method} {Path} failed: {Title} ({ExceptionType})",
            context.Request.Method, context.Request.Path, title, exception.GetType().Name);

        context.Response.StatusCode = status;
        if (retryAfter is not null)
            context.Response.Headers.RetryAfter = ((int)retryAfter.Value.TotalSeconds).ToString();

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Type = type, Title = title, Detail = detail }
        });
    }
}
