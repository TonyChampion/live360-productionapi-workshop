using Microsoft.Net.Http.Headers;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// An ETag identifies one version of a resource. Any change to the ticket (including a new
/// comment) moves UpdatedAt, so the tag changes with it.
/// </summary>
public static class ETags
{
    public static EntityTagHeaderValue For(Ticket ticket) => new($"\"{ticket.UpdatedAt.UtcTicks:x}\"");

    public static void SetETag(this HttpResponse response, EntityTagHeaderValue etag) =>
        response.GetTypedHeaders().ETag = etag;

    /// <summary>GET: does the caller already have this version? (If-None-Match)</summary>
    public static bool HasCurrentVersion(this HttpRequest request, EntityTagHeaderValue etag) =>
        request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false));

    /// <summary>PATCH: is the caller's change based on an old version? (If-Match)</summary>
    public static bool IsBasedOnStaleVersion(this HttpRequest request, EntityTagHeaderValue etag)
    {
        var ifMatch = request.GetTypedHeaders().IfMatch;
        return ifMatch.Count > 0 &&
               !ifMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: true));
    }
}
