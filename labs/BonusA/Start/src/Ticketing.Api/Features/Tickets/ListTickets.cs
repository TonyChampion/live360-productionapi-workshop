using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Infrastructure;

namespace Ticketing.Api.Features.Tickets;

/// <summary>
/// Query string parameters. Every one is optional (nullable), so existing callers keep working
/// unchanged. A class with properties rather than a positional record: OpenAPI generation can't
/// yet read validation attributes on record constructor parameters bound with [AsParameters].
/// </summary>
public class ListTicketsQuery
{
    public const int DefaultPageSize = 25;

    // Explicit names keep the query string (and the OpenAPI document) camelCase
    [FromQuery(Name = "page"), Range(1, int.MaxValue)]
    public int? Page { get; init; }

    [FromQuery(Name = "pageSize"), Range(1, 100)]
    public int? PageSize { get; init; }

    [FromQuery(Name = "status")]
    public TicketStatus? Status { get; init; }

    [FromQuery(Name = "priority")]
    public TicketPriority? Priority { get; init; }

    [FromQuery(Name = "assigneeId")]
    public string? AssigneeId { get; init; }
}

public static class ListTickets
{
    public static RouteGroupBuilder MapListTickets(this RouteGroupBuilder group)
    {
        group.MapGet("/", Handle).WithName("ListTickets");
        return group;
    }

    public static async Task<Ok<List<TicketResponse>>> Handle(
        [AsParameters] ListTicketsQuery query, ITicketStore store, ClaimsPrincipal user,
        HttpContext http, CancellationToken cancellationToken)
    {
        var tickets = await store.ListAsync(cancellationToken);

        // Agents see everything; requesters see only their own tickets
        IEnumerable<Ticket> visible = user.IsAgent()
            ? tickets
            : tickets.Where(t => t.RequesterId == user.UserId());

        if (query.Status is not null) visible = visible.Where(t => t.Status == query.Status);
        if (query.Priority is not null) visible = visible.Where(t => t.Priority == query.Priority);
        if (query.AssigneeId is not null) visible = visible.Where(t => t.AssigneeId == query.AssigneeId);

        // A stable order (with a unique tiebreaker) is what makes paging correct:
        // without it, the same ticket can appear on two pages, or on none
        var ordered = visible.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();

        var pageNumber = query.Page ?? 1;
        var pageSize = query.PageSize ?? ListTicketsQuery.DefaultPageSize;
        var page = ordered
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(t => t.ToResponse())
            .ToList();

        http.Response.Headers["X-Total-Count"] = ordered.Count.ToString();
        var links = PageLinks(http.Request, pageNumber, pageSize, ordered.Count);
        if (links.Count > 0)
            http.Response.Headers.Link = string.Join(", ", links);

        return TypedResults.Ok(page);
    }

    /// <summary>RFC 8288 Link header entries for the neighboring pages.</summary>
    private static List<string> PageLinks(HttpRequest request, int page, int pageSize, int total)
    {
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var links = new List<string>();
        if (page < lastPage) links.Add($"<{UrlForPage(request, page + 1)}>; rel=\"next\"");
        if (page > 1) links.Add($"<{UrlForPage(request, page - 1)}>; rel=\"prev\"");
        return links;
    }

    private static string UrlForPage(HttpRequest request, int page)
    {
        var parameters = request.Query
            .Where(q => !q.Key.Equals("page", StringComparison.OrdinalIgnoreCase))
            .Select(q => KeyValuePair.Create(q.Key, q.Value.ToString()))
            .Append(KeyValuePair.Create("page", page.ToString()));

        return UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path,
            QueryString.Create(parameters!));
    }
}
