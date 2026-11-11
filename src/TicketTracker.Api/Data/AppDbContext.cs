using Microsoft.EntityFrameworkCore;
using TicketTracker.Api.Domain;

namespace TicketTracker.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var ticket = modelBuilder.Entity<Ticket>();

        ticket.ToTable("Tickets");
        ticket.HasKey(t => t.Id);

        ticket.Property(t => t.Title)
            .HasMaxLength(Ticket.TitleMaxLength)
            .IsRequired();

        ticket.Property(t => t.Description)
            .HasMaxLength(Ticket.DescriptionMaxLength);

        // Enums stored as readable strings ("InProgress") rather than magic ints, so the
        // data is self-describing for reporting and ad-hoc queries.
        ticket.Property(t => t.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        ticket.Property(t => t.Priority)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // SQL Server's datetime2 has no offset, so values come back as DateTimeKind.Unspecified.
        // Re-tag them as UTC so they serialize with a trailing "Z" and clients don't treat them as local.
        ticket.Property(t => t.CreatedAt)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
            .IsRequired();

        ticket.Property(t => t.UpdatedAt)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
            .IsRequired();

        // The list endpoint filters by status and sorts by newest first.
        ticket.HasIndex(t => new { t.Status, t.CreatedAt });
        // Supports the "newest first" sort when no status filter is applied.
        ticket.HasIndex(t => t.CreatedAt);
        // Supports the per-priority aggregate in the summary endpoint.
        ticket.HasIndex(t => t.Priority);
    }
}
