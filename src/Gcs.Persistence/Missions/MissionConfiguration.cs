using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gcs.Persistence.Missions;

/// <summary>
/// Missions are always read and written as a whole (the planner edits the full list), so the items are stored as one
/// JSONB document in the mission row instead of a separate table: one round trip, and the order is the array order.
/// </summary>
internal sealed class MissionConfiguration : IEntityTypeConfiguration<Mission>
{
    private const int EnumColumnLength = 16;

    public void Configure(EntityTypeBuilder<Mission> builder)
    {
        builder.ToTable("missions");
        builder.HasKey(m => m.Id);
        builder.Ignore(m => m.DomainEvents);
        builder.Ignore(m => m.TotalDistanceMetres);

        builder.Property(m => m.Id).HasConversion(id => id.Value, value => new MissionId(value)).ValueGeneratedNever();
        builder.Property(m => m.Name).HasMaxLength(Mission.MaxNameLength).IsRequired();
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(m => m.Version).IsConcurrencyToken();

        builder.OwnsMany(m => m.Items, items =>
        {
            items.ToJson("items");
            items.Property(i => i.Command).HasConversion<string>();
            items.Property(i => i.Latitude);
            items.Property(i => i.Longitude);
            items.Property(i => i.Altitude);
            items.Property(i => i.HoldSeconds);
            items.Property(i => i.Speed);
        });
        builder.Navigation(m => m.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsOne(m => m.LastUpload, upload =>
        {
            upload.ToJson("last_upload");
            upload.Property(u => u.VehicleId).HasConversion(id => id.Value, value => new VehicleId(value));
            upload.Property(u => u.Succeeded);
            upload.Property(u => u.At);
            upload.Property(u => u.Error);
        });

        builder.HasIndex(m => m.UpdatedAt).HasDatabaseName("ix_missions_updated_at");
    }
}
