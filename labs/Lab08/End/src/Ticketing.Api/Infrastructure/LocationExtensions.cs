using Microsoft.AspNetCore.Http.Extensions;

namespace Ticketing.Api.Infrastructure;

public static class LocationExtensions
{
    /// <summary>
    /// Absolute URL of a resource created under the current request path,
    /// e.g. POST https://host/api/v1/tickets -> https://host/api/v1/tickets/{id}.
    /// Absolute, because that's what the controller API returned and callers may rely on it.
    /// </summary>
    public static Uri LocationFor(this HttpRequest request, object id)
        => new($"{request.GetEncodedUrl().TrimEnd('/')}/{id}");
}
