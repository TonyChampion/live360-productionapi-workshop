namespace Ticketing.Api.Infrastructure;

public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Headers that tell browsers how to treat our responses. Set in OnStarting so they're
    /// applied to every response, including errors written after the pipeline has failed.
    /// </summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";   // don't guess content types
                headers["Referrer-Policy"] = "no-referrer";

                // API responses are data, never pages: nothing may load, nothing may frame them.
                // (The lab client is a page with its own scripts, so it doesn't get this policy.)
                if (context.Request.Path.StartsWithSegments("/api"))
                    headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

                return Task.CompletedTask;
            });

            await next();
        });

        return app;
    }
}
