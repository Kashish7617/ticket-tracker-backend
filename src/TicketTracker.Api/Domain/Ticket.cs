namespace TicketTracker.Api.Domain;

public enum TicketStatus
{
    Open = 0,
    InProgress = 1,
    Closed = 2,
}

public enum TicketPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
}

public class Ticket
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 4000;

    public int Id { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedAt { get; set; }
}
