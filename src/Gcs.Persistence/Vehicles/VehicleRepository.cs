using Gcs.Application.Abstractions;
using Gcs.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace Gcs.Persistence.Vehicles;

internal sealed class VehicleRepository(GcsDbContext db) : IVehicleRepository
{
    public Task<Vehicle?> GetByIdAsync(VehicleId id, CancellationToken cancellationToken) =>
        db.Vehicles.SingleOrDefaultAsync(v => v.Id == id, cancellationToken);

    public Task<bool> IsCallsignInUseAsync(Callsign callsign, VehicleId? excluding, CancellationToken cancellationToken) =>
        db.Vehicles.AnyAsync(
            v => v.Status == VehicleStatus.Active && v.Callsign == callsign && (excluding == null || v.Id != excluding),
            cancellationToken);

    public Task<bool> IsSystemIdInUseAsync(MavlinkSystemId systemId, VehicleId? excluding, CancellationToken cancellationToken) =>
        db.Vehicles.AnyAsync(
            v => v.Status == VehicleStatus.Active && v.SystemId == systemId && (excluding == null || v.Id != excluding),
            cancellationToken);

    public void Add(Vehicle vehicle) => db.Vehicles.Add(vehicle);
}
