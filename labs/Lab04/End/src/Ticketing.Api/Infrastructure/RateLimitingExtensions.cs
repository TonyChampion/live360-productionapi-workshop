using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ticketing.Api.Infrastructure;

public static class RateLimitPolicies
{
    public const string CreateTicket = "create-ticket";
}

/// <summary>Bound from RateLimits:CreateTicket, so limits change without a redeploy.</summary>
public class CreateTicketLimits
{
    public int PermitLimit { get; set; } = 20;
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddTicketingRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CreateTicketLimits>(configuration.GetSection("RateLimits:CreateTicket"));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // One fixed-window bucket per signed-in user (or per IP for anonymous callers)
            options.AddPolicy(RateLimitPolicies.CreateTicket, http =>
            {
                var limits = http.RequestServices.GetRequiredService<IOptions<CreateTicketLimits>>().Value;
                var partitionKey = http.User.Identity?.IsAuthenticated == true
                    ? $"user:{http.User.UserId()}"
                    : $"ip:{http.Connection.RemoteIpAddress}";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0
                });
            });

            // A rejection is a designed failure too: tell the caller when to come back
            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Type = ProblemTypes.RateLimited,
                        Title = "Too many requests",
                        Detail = "You've hit the limit for this action. Try again after the Retry-After interval."
                    }
                });
            };
        });

        return services;
    }
}
