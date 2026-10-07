using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gcs.Persistence.Telemetry;

internal sealed class TelemetrySampleConfiguration : IEntityTypeConfiguration<TelemetrySampleRecord>
{
    private const int ShortTextLength = 32;

    public void Configure(EntityTypeBuilder<TelemetrySampleRecord> builder)
    {
        builder.ToTable("telemetry_samples");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).UseIdentityAlwaysColumn();
        builder.Property(s => s.GpsFix).HasMaxLength(ShortTextLength);
        builder.Property(s => s.FlightMode).HasMaxLength(ShortTextLength);

        // Every query is "one vehicle, a time range", and retention deletes by time. A composite index serves both.
        // No foreign key to vehicles: history must be writable at full speed and outlive its vehicle row.
        builder.HasIndex(s => new { s.VehicleId, s.RecordedAt }).HasDatabaseName("ix_telemetry_samples_vehicle_time");
        builder.HasIndex(s => s.RecordedAt).HasDatabaseName("ix_telemetry_samples_time");
    }
}
