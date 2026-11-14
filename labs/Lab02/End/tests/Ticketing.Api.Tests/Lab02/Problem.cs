using System.Text.Json;

namespace Ticketing.Api.Tests.Lab02;

/// <summary>
/// Reads an error response as RFC 9457 Problem Details, failing the test if it isn't one.
/// </summary>
public static class Problem
{
    public const string Base = "https://ticketing.example/problems/";
    public const string Validation = Base + "validation";
    public const string TicketNotFound = Base + "ticket-not-found";
    public const string InvalidTransition = Base + "invalid-transition";
    public const string NotFound = Base + "not-found";
    public const string Unexpected = Base + "unexpected";

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static string? Str(this JsonElement problem, string name) =>
        problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The validation errors for a field. Matched exactly: error keys must use the same
    /// names as the JSON the caller sent (camelCase), or a client can't map them to its fields.
    /// </summary>
    public static string[] ErrorsFor(this JsonElement problem, string field)
    {
        if (!problem.TryGetProperty("errors", out var errors)) return [];
        return errors.TryGetProperty(field, out var messages)
            ? messages.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];
    }
}
