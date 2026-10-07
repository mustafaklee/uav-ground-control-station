using Gcs.Application.Abstractions;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.UnitTests.Application.Vehicles;

/// <summary>In-memory stand-in for the PostgreSQL repository. Behaves like the real one for the rules under test.</summary>
internal sealed class FakeVehicleRepository : IVehicleRepository
{
    public List<Vehicle> Vehicles { get; } = [];

    public Task<Vehicle?> GetByIdAsync(VehicleId id, CancellationToken cancellationToken) =>
        Task.FromResult(Vehicles.SingleOrDefault(v => v.Id == id));

    public Task<bool> IsCallsignInUseAsync(Callsign callsign, VehicleId? excluding, CancellationToken cancellationToken) =>
        Task.FromResult(Vehicles.Any(v => !v.IsRetired && v.Callsign == callsign && v.Id != excluding));

    public Task<bool> IsSystemIdInUseAsync(MavlinkSystemId systemId, VehicleId? excluding, CancellationToken cancellationToken) =>
        Task.FromResult(Vehicles.Any(v => !v.IsRetired && v.SystemId == systemId && v.Id != excluding));

    public void Add(Vehicle vehicle) => Vehicles.Add(vehicle);
}

/// <summary>Records saves and can simulate database-level failures.</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Exception? FailWith { get; set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Records link requests instead of opening real MAVLink connections.</summary>
internal sealed class FakeLinkManager : IVehicleLinkManager
{
    public List<VehicleId> Disconnected { get; } = [];

    public Task<Result> ConnectAsync(VehicleLinkTarget target, CancellationToken cancellationToken) => Task.FromResult(Result.Success());

    public Task DisconnectAsync(VehicleId vehicleId, CancellationToken cancellationToken)
    {
        Disconnected.Add(vehicleId);
        return Task.CompletedTask;
    }

    public VehicleLinkStatus? GetStatus(VehicleId vehicleId) => null;
}
