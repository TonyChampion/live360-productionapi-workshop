using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Services;

public interface ITicketService
{
    Task<Ticket?> GetTicketAsync(Guid id);
    Task<IReadOnlyList<Ticket>> GetTicketsAsync();
    Task<Ticket> CreateTicketAsync(Ticket ticket);
    Task<IReadOnlyList<Comment>?> GetCommentsAsync(Guid ticketId);
    Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment);
}

/// <summary>
/// The classic "service layer": mostly passes calls through to the store.
/// </summary>
public class TicketService(ITicketStore store) : ITicketService
{
    public Task<Ticket?> GetTicketAsync(Guid id) => store.GetAsync(id);

    public Task<IReadOnlyList<Ticket>> GetTicketsAsync() => store.ListAsync();

    public async Task<Ticket> CreateTicketAsync(Ticket ticket)
    {
        ticket.Id = Guid.NewGuid();
        ticket.Status = TicketStatus.Open;
        ticket.CreatedAt = ticket.UpdatedAt = DateTimeOffset.UtcNow;
        await store.AddAsync(ticket);
        return ticket;
    }

    public async Task<IReadOnlyList<Comment>?> GetCommentsAsync(Guid ticketId)
    {
        var ticket = await store.GetAsync(ticketId);
        return ticket?.Comments;
    }

    public Task<Comment?> AddCommentAsync(Guid ticketId, Comment comment)
    {
        comment.Id = Guid.NewGuid();
        comment.CreatedAt = DateTimeOffset.UtcNow;
        return store.AddCommentAsync(ticketId, comment);
    }
}
