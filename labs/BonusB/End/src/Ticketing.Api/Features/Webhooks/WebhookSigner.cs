using System.Security.Cryptography;
using System.Text;

namespace Ticketing.Api.Features.Webhooks;

/// <summary>
/// Standard Webhooks signing (standardwebhooks.com), so receivers can use an off-the-shelf verifier.
/// The signature covers the id and timestamp as well as the body: change any of them, and it no longer matches.
/// </summary>
public static class WebhookSigner
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    public static string Sign(string secret, string id, long timestamp, string body)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
        return "v1," + Convert.ToBase64String(signature);
    }
}
