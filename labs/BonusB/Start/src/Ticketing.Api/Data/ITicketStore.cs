using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

/// <summary>
/// The only way the API reaches ticket data. Everything behind this boundary
/// can be swapped (in-memory today, a database tomorrow) or wrapped (fault injection).
/// </summary>
public interface ITicketStore
{
    Task<Ticket?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Ticket>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default);
    Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken = default);
    Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment, CancellationToken cancellationToken = default);
}
