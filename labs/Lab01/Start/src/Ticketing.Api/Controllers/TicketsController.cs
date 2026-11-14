using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Route("api/v1/tickets")]
public class TicketsController(ITicketService tickets) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Ticket>>> GetAll()
        => Ok(await tickets.GetTicketsAsync());

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Ticket>> Get(Guid id)
    {
        var ticket = await tickets.GetTicketAsync(id);
        return ticket is null ? NotFound() : Ok(ticket);
    }

    [HttpPost]
    public async Task<ActionResult<Ticket>> Create(Ticket ticket)
    {
        var created = await tickets.CreateTicketAsync(ticket);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }
}
