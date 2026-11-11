using Microsoft.EntityFrameworkCore;
using TicketTracker.Api.Contracts;
using TicketTracker.Api.Data;
using TicketTracker.Api.Domain;
using TicketTracker.Api.Infrastructure;

namespace TicketTracker.Api.Services;

public interface ITicketService
{
    Task<PagedResult<TicketResponse>> ListAsync(TicketListQuery query, CancellationToken ct);
    Task<TicketResponse?> GetAsync(int id, CancellationToken ct);
    Task<TicketResponse> CreateAsync(CreateTicketRequest request, CancellationToken ct);
    Task<TicketResponse?> UpdateAsync(int id, UpdateTicketRequest request, CancellationToken ct);
    Task<TicketResponse?> UpdateStatusAsync(int id, TicketStatus status, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
    Task<TicketSummaryResponse> GetSummaryAsync(CancellationToken ct);
}

public class TicketService(AppDbContext db, TimeProvider clock) : ITicketService
{
    public async Task<PagedResult<TicketResponse>> ListAsync(TicketListQuery query, CancellationToken ct)
    {
        IQueryable<Ticket> tickets = db.Tickets.AsNoTracking();

        if (query.ParsedStatus is { } status)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Parameterised LIKE with the user's wildcard characters escaped, so "50%" matches
            // literally. Case-insensitivity comes from the column's collation.
            var pattern = $"%{EscapeLike(query.Search.Trim())}%";
            tickets = tickets.Where(t => EF.Functions.Like(t.Title, pattern, LikeEscape));
        }

        var totalCount = await tickets.CountAsync(ct);

        var items = await tickets
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id) // deterministic order for stable paging
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => new TicketResponse(
                t.Id, t.Title, t.Description, t.Status, t.Priority, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);

        return new PagedResult<TicketResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<TicketResponse?> GetAsync(int id, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        return ticket is null ? null : TicketResponse.From(ticket);
    }

    public async Task<TicketResponse> CreateAsync(CreateTicketRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ticket = new Ticket
        {
            Title = request.Title.Trim(),
            Description = NormalizeDescription(request.Description),
            Status = request.Status ?? TicketStatus.Open,
            Priority = request.Priority!.Value, // [Required] guarantees a value
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        return TicketResponse.From(ticket);
    }

    public async Task<TicketResponse?> UpdateAsync(int id, UpdateTicketRequest request, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null) return null;

        ticket.Title = request.Title.Trim();
        ticket.Description = NormalizeDescription(request.Description);
        ticket.Status = request.Status!.Value;
        ticket.Priority = request.Priority!.Value;
        ticket.UpdatedAt = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
        return TicketResponse.From(ticket);
    }

    public async Task<TicketResponse?> UpdateStatusAsync(int id, TicketStatus status, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null) return null;

        if (ticket.Status != status)
        {
            ticket.Status = status;
            ticket.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        return TicketResponse.From(ticket);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        // Single round trip: DELETE ... WHERE Id = @id, no need to load the entity first.
        var deleted = await db.Tickets.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    public async Task<TicketSummaryResponse> GetSummaryAsync(CancellationToken ct)
    {
        // Both aggregates run as GROUP BY queries in SQL Server; only a handful of
        // (key, count) rows come back, never the tickets themselves.
        var byStatus = await db.Tickets
            .GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var byPriority = await db.Tickets
            .GroupBy(t => t.Priority)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new TicketSummaryResponse(
            Total: byStatus.Values.Sum(),
            ByStatus: WithAllKeys(byStatus),
            ByPriority: WithAllKeys(byPriority));
    }

    private const char LikeEscapeChar = '\\';
    private const string LikeEscape = "\\";

    private static string EscapeLike(string input) =>
        input
            .Replace(LikeEscape, $"{LikeEscapeChar}{LikeEscapeChar}")
            .Replace("%", $"{LikeEscapeChar}%")
            .Replace("_", $"{LikeEscapeChar}_")
            .Replace("[", $"{LikeEscapeChar}[");

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>Zero-fills enum values that have no rows so clients always get every key.</summary>
    private static Dictionary<string, int> WithAllKeys<TEnum>(Dictionary<TEnum, int> counts)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().ToDictionary(
            EnumNames.ToWireName,
            v => counts.GetValueOrDefault(v));
}
