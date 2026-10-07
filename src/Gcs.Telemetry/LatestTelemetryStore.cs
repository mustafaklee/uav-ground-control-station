using System.Collections.Concurrent;
using Gcs.Application.Abstractions;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;

namespace Gcs.Telemetry;

/// <summary>
/// In-memory latest-state cache: one snapshot per vehicle, updated on every telemetry message.
/// Writes come from the link receive loops (one per vehicle), reads from API requests and the broadcaster, concurrently.
/// Snapshots are immutable records, so a reader always sees a complete, consistent snapshot.
/// </summary>
/// <remarks>Redis is deliberately not used here; see ADR-008 and ADR-011.</remarks>
internal sealed class LatestTelemetryStore : ITelemetryService
{
    private readonly ConcurrentDictionary<VehicleId, TelemetrySnapshot> _snapshots = new();

    public TelemetrySnapshot Apply(VehicleId vehicleId, TelemetryUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return _snapshots.AddOrUpdate(
            vehicleId,
            static (id, u) => TelemetrySnapshot.Empty(id, u.ReceivedAt).Apply(u),
            static (_, current, u) => current.Apply(u),
            update);
    }

    public TelemetrySnapshot? GetLatest(VehicleId vehicleId) =>
        _snapshots.TryGetValue(vehicleId, out var snapshot) ? snapshot : null;

    public IEnumerable<TelemetrySnapshot> All() => _snapshots.Values;
}
