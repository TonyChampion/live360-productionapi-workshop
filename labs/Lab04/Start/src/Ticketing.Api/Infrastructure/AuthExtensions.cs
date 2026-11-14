using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Ticketing.Api.Features.Tickets;

namespace Ticketing.Api.Infrastructure;

public static class Roles
{
    public const string Requester = "requester";
    public const string Agent = "agent";
    public const string Admin = "admin";
}

public static class Policies
{
    /// <summary>Agents and admins: assign, close, and see every ticket.</summary>
    public const string Agent = "Agent";

    /// <summary>Resource-based: may this user touch this particular ticket?</summary>
    public const string TicketAccess = "TicketAccess";
}

public static class AuthExtensions
{
    private const string BearerOrApiKey = "BearerOrApiKey";

    public static IServiceCollection AddTicketingAuth(this IServiceCollection services)
    {
        services.AddAuthentication(BearerOrApiKey)
            // Reads Authentication:Schemes:Bearer from configuration (dotnet user-jwts writes it)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(ApiKeyDefaults.Scheme, null)
            // Not a real scheme: picks one per request, so every policy accepts either
            .AddPolicyScheme(BearerOrApiKey, "Bearer token or API key", options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ApiKeyDefaults.Header)
                        ? ApiKeyDefaults.Scheme
                        : JwtBearerDefaults.AuthenticationScheme);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, p => p.RequireRole(Roles.Agent, Roles.Admin))
            .AddPolicy(Policies.TicketAccess, p => p.AddRequirements(new TicketAccessRequirement()));

        services.AddSingleton<IAuthorizationHandler, TicketAccessHandler>();

        return services;
    }

    public static bool IsAgent(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Agent) || user.IsInRole(Roles.Admin);

    /// <summary>The signed-in user's id (the token's unique_name / sub).</summary>
    public static string UserId(this ClaimsPrincipal user) => user.Identity?.Name ?? "";
}
