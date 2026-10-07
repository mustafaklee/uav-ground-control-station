using Gcs.Application.Abstractions;
using Gcs.Contracts.Common;
using Gcs.Domain.Commands;
using Gcs.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gcs.Persistence.Commands;

/// <summary>
/// The audit log table. No foreign key to vehicles on purpose: the log must outlive any change to the vehicle table,
/// and the callsign is copied into each row. The only query is "one vehicle, newest first", served by one index.
/// </summary>
internal sealed class CommandAuditConfiguration : IEntityTypeConfiguration<CommandAuditEntry>
{
    private const int EnumColumnLength = 16;
    private const int ShortTextLength = 64;

    public void Configure(EntityTypeBuilder<CommandAuditEntry> builder)
    {
        builder.ToTable("command_audit");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasConversion(id => id.Value, value => new CommandAuditId(value)).ValueGeneratedNever();
        builder.Property(e => e.VehicleId).HasConversion(id => id.Value, value => new VehicleId(value));
        builder.Property(e => e.Callsign).HasMaxLength(Callsign.MaxLength).IsRequired();
        builder.Property(e => e.Operator).HasMaxLength(OperatorName.MaxLength).IsRequired();
        builder.Property(e => e.Command).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.Parameters).HasMaxLength(ShortTextLength);
        builder.Property(e => e.Source).HasMaxLength(ShortTextLength).IsRequired();
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.Detail).HasMaxLength(CommandAuditEntry.MaxDetailLength);

        builder.HasIndex(e => new { e.VehicleId, e.RequestedAt }).HasDatabaseName("ix_command_audit_vehicle_time");
    }
}

internal sealed class CommandAuditLog(GcsDbContext db) : ICommandAuditLog
{
    public void Add(CommandAuditEntry entry) => db.CommandAudit.Add(entry);
}

internal sealed class CommandAuditQueries(GcsDbContext db) : ICommandAuditQueries
{
    public async Task<PagedResponse<CommandAuditEntry>> ListAsync(
        VehicleId vehicleId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.CommandAudit.AsNoTracking().Where(e => e.VehicleId == vehicleId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(e => e.RequestedAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResponse<CommandAuditEntry>(items, page, pageSize, total);
    }
}
