namespace Ticketing.Api.Infrastructure;

public static class CorsPolicies
{
    public const string SupportSite = "SupportSite";
}

public static class CorsExtensions
{
    /// <summary>
    /// Which browser origins may call the API, from Cors:AllowedOrigins. CORS only affects browsers;
    /// it is not access control (curl and servers ignore it). Authentication still applies.
    /// </summary>
    public static IServiceCollection AddSupportSiteCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(CorsPolicies.SupportSite, policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PATCH")
            .WithHeaders("Authorization", "Content-Type", ApiKeyDefaults.Header, "If-Match", "If-None-Match")
            // Browsers hide response headers from cross-origin pages unless they're listed here
            .WithExposedHeaders("X-Total-Count", "Link", "ETag", "Retry-After")));

        return services;
    }
}
