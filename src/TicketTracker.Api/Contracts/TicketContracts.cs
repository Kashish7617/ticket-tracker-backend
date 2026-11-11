using System.ComponentModel.DataAnnotations;
using TicketTracker.Api.Domain;
using TicketTracker.Api.Infrastructure;

namespace TicketTracker.Api.Contracts;

/// <summary>A support ticket as returned by the API.</summary>
public record TicketResponse(
    int Id,
    string Title,
    string? Description,
    TicketStatus Status,
    TicketPriority Priority,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static TicketResponse From(Ticket t) =>
        new(t.Id, t.Title, t.Description, t.Status, t.Priority, t.CreatedAt, t.UpdatedAt);
}

public record CreateTicketRequest
{
    /// <summary>Short summary of the problem. Required, max 120 characters.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(Ticket.TitleMaxLength)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Required: low, medium or high.</summary>
    [Required]
    public TicketPriority? Priority { get; init; }

    /// <summary>Optional, defaults to open.</summary>
    public TicketStatus? Status { get; init; }
}

/// <summary>Full replacement of a ticket's editable fields.</summary>
public record UpdateTicketRequest
{
    [Required(AllowEmptyStrings = false)]
    [MaxLength(Ticket.TitleMaxLength)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    [Required]
    public TicketStatus? Status { get; init; }

    [Required]
    public TicketPriority? Priority { get; init; }
}

public record UpdateTicketStatusRequest
{
    [Required]
    public TicketStatus? Status { get; init; }
}

/// <summary>Query-string parameters for listing tickets.</summary>
public class TicketListQuery : IValidatableObject
{
    public const int MaxPageSize = 100;

    /// <summary>Filter by status: open, in_progress or closed.</summary>
    public string? Status { get; init; }

    /// <summary>Case-insensitive "contains" match on the title.</summary>
    [MaxLength(Ticket.TitleMaxLength)]
    public string? Search { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; init; } = 10;

    public TicketStatus? ParsedStatus =>
        EnumNames.TryParse<TicketStatus>(Status, out var s) ? s : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(Status) && ParsedStatus is null)
        {
            yield return new ValidationResult(
                $"Status must be one of: {string.Join(", ", EnumNames.WireNames<TicketStatus>())}.",
                [nameof(Status)]);
        }
    }
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Ticket counts per status and per priority. Every enum value is always present.</summary>
public record TicketSummaryResponse(
    int Total,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, int> ByPriority);
