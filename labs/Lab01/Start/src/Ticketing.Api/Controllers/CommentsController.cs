using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Route("api/v1/tickets/{ticketId:guid}/comments")]
public class CommentsController(ITicketService tickets) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Comment>>> GetAll(Guid ticketId)
    {
        var comments = await tickets.GetCommentsAsync(ticketId);
        return comments is null ? NotFound() : Ok(comments);
    }

    [HttpPost]
    public async Task<ActionResult<Comment>> Add(Guid ticketId, Comment comment)
    {
        var added = await tickets.AddCommentAsync(ticketId, comment);
        return added is null
            ? NotFound()
            : Created($"/api/v1/tickets/{ticketId}/comments/{added.Id}", added);
    }
}
