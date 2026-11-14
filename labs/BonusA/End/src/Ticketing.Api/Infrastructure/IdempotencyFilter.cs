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
