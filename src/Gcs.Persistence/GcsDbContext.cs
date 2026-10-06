using Microsoft.EntityFrameworkCore;

namespace Gcs.Persistence;

/// <summary>
/// EF Core unit of work for the GCS PostgreSQL database. Entity configurations are added per module
/// (Vehicles in Phase 2, Missions in Phase 6, ...) as <see cref="IEntityTypeConfiguration{TEntity}"/> classes in this assembly.
/// </summary>
public sealed class GcsDbContext(DbContextOptions<GcsDbContext> options) : DbContext(options)
{
    public const string Schema = "gcs";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GcsDbContext).Assembly);
    }
}
