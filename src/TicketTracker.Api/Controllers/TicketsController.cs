using Microsoft.AspNetCore.Mvc;
using TicketTracker.Api.Contracts;
using TicketTracker.Api.Services;

namespace TicketTracker.Api.Controllers;

[ApiController]
[Route("api/tickets")]
// Note: no class-level [Produces("application/json")] - it would force error responses to
// "application/json" instead of the correct "application/problem+json".
public class TicketsController(ITicketService tickets) : ControllerBase
{
    /// <summary>Lists tickets, newest first, with optional status filter, title search and paging.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<TicketResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<TicketResponse>>> List(
        [FromQuery] TicketListQuery query, CancellationToken ct) =>
        Ok(await tickets.ListAsync(query, ct));

    /// <summary>Ticket counts per status and per priority, aggregated in the database.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<TicketSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketSummaryResponse>> Summary(CancellationToken ct) =>
        Ok(await tickets.GetSummaryAsync(ct));

    /// <summary>Gets a single ticket.</summary>
    [HttpGet("{id:int}", Name = nameof(Get))]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketResponse>> Get(int id, CancellationToken ct)
    {
        var ticket = await tickets.GetAsync(id, ct);
        return ticket is null ? TicketNotFound(id) : Ok(ticket);
    }

    /// <summary>Creates a ticket. Status defaults to open.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TicketResponse>> Create(CreateTicketRequest request, CancellationToken ct)
    {
        var created = await tickets.CreateAsync(request, ct);
        return CreatedAtRoute(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>Replaces a ticket's title, description, status and priority.</summary>
    [HttpPut("{id:int}")]
    [Consumes("application/json")]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketResponse>> Update(int id, UpdateTicketRequest request, CancellationToken ct)
    {
        var updated = await tickets.UpdateAsync(id, request, ct);
        return updated is null ? TicketNotFound(id) : Ok(updated);
    }

    /// <summary>Changes only the status of a ticket.</summary>
    [HttpPatch("{id:int}/status")]
    [Consumes("application/json")]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketResponse>> UpdateStatus(
        int id, UpdateTicketStatusRequest request, CancellationToken ct)
    {
        var updated = await tickets.UpdateStatusAsync(id, request.Status!.Value, ct);
        return updated is null ? TicketNotFound(id) : Ok(updated);
    }

    /// <summary>Deletes a ticket.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await tickets.DeleteAsync(id, ct) ? NoContent() : TicketNotFound(id);

    private ObjectResult TicketNotFound(int id) =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Ticket not found",
            detail: $"No ticket exists with id {id}.");
}
