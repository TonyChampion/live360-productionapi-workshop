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
