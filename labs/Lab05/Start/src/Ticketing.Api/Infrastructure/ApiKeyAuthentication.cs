using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ticketing.Api.Infrastructure;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";
}

/// <summary>One service caller, from configuration: ApiKeys:{name}:Key and ApiKeys:{name}:Roles.</summary>
public class ApiKeyClient
{
    public string Key { get; set; } = "";
    public string[] Roles { get; set; } = [];
}

/// <summary>
/// Authenticates service callers (dashboards, integrations, scripts) that send X-Api-Key.
/// The caller becomes a ClaimsPrincipal like any other, so every policy applies unchanged.
/// </summary>
public class ApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var supplied = Request.Headers[ApiKeyDefaults.Header].ToString();
        if (string.IsNullOrEmpty(supplied))
            return Task.FromResult(AuthenticateResult.NoResult());

        var clients = configuration.GetSection("ApiKeys").Get<Dictionary<string, ApiKeyClient>>() ?? [];
        foreach (var (name, client) in clients)
        {
            if (!KeysMatch(client.Key, supplied))
                continue;

            var claims = new List<Claim> { new(ClaimTypes.Name, name) };
            claims.AddRange(client.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }

    /// <summary>
    /// Compare hashes in constant time, so response timing reveals nothing about how much of a
    /// guessed key was right (and nothing about its length).
    /// </summary>
    private static bool KeysMatch(string expected, string supplied) =>
        expected.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}
