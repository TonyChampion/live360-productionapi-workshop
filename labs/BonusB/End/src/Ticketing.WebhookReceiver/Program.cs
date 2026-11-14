using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

// The other side of Bonus Lab B: a customer's system receiving the API's webhooks.
//
//   dotnet run -- --Webhook:Secret whsec_...      (the secret the API gave you when you subscribed)
//
//   POST /webhooks          where the API delivers. Verifies the signature, ignores duplicates.
//   POST /fail-next/{n}     the next n deliveries get a 503, so you can watch the API retry
//   GET  /                  everything received so far

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
var app = builder.Build();

var secret = app.Configuration["Webhook:Secret"];
var tolerance = TimeSpan.FromMinutes(5);
var received = new ConcurrentQueue<object>();
var seen = new ConcurrentDictionary<string, bool>();
var failNext = 0;

if (string.IsNullOrEmpty(secret))
    app.Logger.LogWarning("No --Webhook:Secret given: every delivery will be rejected. Subscribe first, then restart with the secret.");

app.MapPost("/webhooks", async (HttpRequest request, ILogger<Program> log) =>
{
    // Read the raw body: the signature covers these exact bytes, not a re-serialized copy
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();

    var id = request.Headers["webhook-id"].ToString();
    var timestamp = request.Headers["webhook-timestamp"].ToString();
    var signatures = request.Headers["webhook-signature"].ToString();

    if (Interlocked.Decrement(ref failNext) >= 0)
    {
        log.LogWarning("{Id}: failing on purpose (503)", id);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    Interlocked.Exchange(ref failNext, 0);

    // 1. Recent? An old timestamp is a replayed capture, even with a valid signature
    if (!long.TryParse(timestamp, out var seconds) ||
        (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > tolerance)
    {
        log.LogWarning("{Id}: rejected, timestamp missing or too old", id);
        return Results.BadRequest();
    }

    // 2. Signed with our secret? Recompute the signature, and compare in constant time
    if (string.IsNullOrEmpty(secret) || !SignatureMatches(secret, id, timestamp, body, signatures))
    {
        log.LogWarning("{Id}: rejected, bad signature", id);
        return Results.Unauthorized();
    }

    // 3. Seen it? Deliveries are at-least-once, so the same id can arrive more than once
    if (!seen.TryAdd(id, true))
    {
        log.LogInformation("{Id}: duplicate, already handled", id);
        return Results.Ok();
    }

    log.LogInformation("{Id}: verified {Body}", id, body);
    received.Enqueue(new { id, receivedAt = DateTimeOffset.UtcNow, body = System.Text.Json.JsonDocument.Parse(body).RootElement });
    return Results.Ok();
});

app.MapPost("/fail-next/{count:int}", (int count) =>
{
    Interlocked.Exchange(ref failNext, count);
    return Results.Ok(new { failNext = count });
});

app.MapGet("/", () => received.ToArray());

app.Run();

// Standard Webhooks (standardwebhooks.com): signature = base64(HMAC-SHA256(key, "{id}.{timestamp}.{body}")),
// sent as "v1,<signature>". The header may hold several, space-separated, while a secret is being rotated.
static bool SignatureMatches(string secret, string id, string timestamp, string body, string header)
{
    var key = Convert.FromBase64String(secret.StartsWith("whsec_") ? secret["whsec_".Length..] : secret);
    var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));

    foreach (var candidate in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = candidate.Split(',', 2);
        if (parts is not ["v1", var value]) continue;
        try
        {
            if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(value)))
                return true;
        }
        catch (FormatException) { }
    }
    return false;
}
