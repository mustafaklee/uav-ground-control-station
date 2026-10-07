using Gcs.Domain.Commands;
using Gcs.Domain.Common;
using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;
using Gcs.Persistence.Outbox;
using Gcs.Persistence.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Gcs.Persistence;

/// <summary>
/// EF Core unit of work for the GCS PostgreSQL database. Entity configurations live next to their module
/// (Vehicles, Outbox, ...) as <see cref="IEntityTypeConfiguration{TEntity}"/> classes in this assembly.
/// </summary>
public sealed class GcsDbContext(DbContextOptions<GcsDbContext> options) : DbContext(options)
{
    public const string Schema = "gcs";

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<Mission> Missions => Set<Mission>();

    public DbSet<CommandAuditEntry> CommandAudit => Set<CommandAuditEntry>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<TelemetrySampleRecord> TelemetrySamples => Set<TelemetrySampleRecord>();

    /// <summary>
    /// Before saving, every domain event raised by a tracked aggregate is turned into an outbox row.
    /// Both go to PostgreSQL in the same transaction: the change and its event are saved together or not at all.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var aggregates = ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        OutboxMessages.AddRange(aggregates
            .SelectMany(aggregate => aggregate.DomainEvents)
            .Select(DomainEventSerializer.ToOutboxMessage));

        var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        // Only forget the events once they are safely stored.
        aggregates.ForEach(aggregate => aggregate.ClearDomainEvents());
        return written;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GcsDbContext).Assembly);
    }
}
