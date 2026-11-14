using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

public static class SeedData
{
    public static readonly Guid FirstTicketId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static IEnumerable<Ticket> Tickets()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-14);
        yield return new Ticket
        {
            Id = FirstTicketId,
            Title = "Cannot reset password",
            Description = "Reset email never arrives.",
            Priority = TicketPriority.High,
            RequesterId = "alice",
            CreatedAt = start,
            UpdatedAt = start,
            Comments = [new Comment { TicketId = FirstTicketId, AuthorId = "alice", Body = "Checked spam too.", CreatedAt = start.AddMinutes(5) }]
        };

        string[] titles =
        [
            "Invoice PDF is blank", "Dashboard loads slowly", "Export to CSV fails",
            "Typo on billing page", "SSO login loops", "Mobile app crashes on launch",
            "Notifications sent twice", "Search ignores filters", "Cannot upload avatar"
        ];
        string[] requesters = ["alice", "bob", "carol"];

        for (var i = 0; i < titles.Length; i++)
        {
            var created = start.AddHours(6 * (i + 1));
            yield return new Ticket
            {
                Title = titles[i],
                Description = $"Reported issue: {titles[i].ToLowerInvariant()}.",
                Priority = (TicketPriority)(i % 4),
                Status = (TicketStatus)(i % 3),
                RequesterId = requesters[i % requesters.Length],
                AssigneeId = i % 2 == 0 ? "dana" : null,
                CreatedAt = created,
                UpdatedAt = created
            };
        }
    }
}
