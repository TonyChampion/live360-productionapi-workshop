using System.Security.Cryptography;
using System.Text;

namespace Ticketing.Api.Infrastructure;

public static class IdempotencyHeaders
{
    public const string Key = "Idempotency-Key";
    public const string Replayed = "Idempotent-Replayed";
}

public static class IdempotencyKeys
{
    /// <summary>
    /// The same caller and key always give the same id, on any instance, after any restart.
    /// Keys are scoped to the caller, so two users who happen to pick the same key get two tickets.
    /// </summary>
    public static Guid IdFor(string userId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\n{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
