using Gcs.Application.Vehicles;
using Gcs.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gcs.Persistence.Vehicles;

/// <summary>
/// How a <see cref="Vehicle"/> is stored. Kept here, not as attributes on the entity, so the domain stays free of
/// database concerns. Column names become snake_case through the naming convention (callsign, system_id, ...).
/// </summary>
internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    private const int EnumColumnLength = 16;

    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("vehicles");
        builder.HasKey(v => v.Id);
        builder.Ignore(v => v.DomainEvents);

        builder.Property(v => v.Id)
            .HasConversion(id => id.Value, value => new VehicleId(value))
            .ValueGeneratedNever();

        builder.Property(v => v.Callsign)
            .HasConversion(callsign => callsign.Value, value => Callsign.Create(value).Value)
            .HasMaxLength(Callsign.MaxLength)
            .IsRequired();

        builder.Property(v => v.SystemId)
            .HasConversion(id => (short)id.Value, value => MavlinkSystemId.Create(value).Value)
            .IsRequired();

        // Enums as text: readable in SQL and safe if enum members are ever reordered.
        builder.Property(v => v.Autopilot).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(v => v.Type).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(EnumColumnLength);

        builder.ComplexProperty(v => v.Connection, connection =>
        {
            connection.Property(c => c.Transport).HasConversion<string>().HasMaxLength(EnumColumnLength);
            // The value object has get-only properties, so each one is mapped explicitly.
            connection.Property(c => c.Host).HasMaxLength(ConnectionSettings.MaxHostLength);
            connection.Property(c => c.Port);
            connection.Property(c => c.SerialPortName).HasMaxLength(ConnectionSettings.MaxSerialPortNameLength);
            connection.Property(c => c.BaudRate);
        });

        // Optimistic concurrency: UPDATE ... WHERE id = @id AND version = @originalVersion.
        // If another request saved first, zero rows match and EF Core raises DbUpdateConcurrencyException.
        builder.Property(v => v.Version).IsConcurrencyToken();

        // Partial unique indexes: only active vehicles reserve a callsign / system id,
        // so a retired UAV-01 does not block registering a new UAV-01.
        builder.HasIndex(v => v.Callsign)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName(VehicleConstraints.ActiveCallsignUnique);

        builder.HasIndex(v => v.SystemId)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName(VehicleConstraints.ActiveSystemIdUnique);
    }
}
