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
