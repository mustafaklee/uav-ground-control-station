using Gcs.Contracts.Commands;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;

namespace Gcs.Application.Abstractions;

/// <summary>
/// Pushes live data to connected operator clients. Implemented by the API host with SignalR; the telemetry and link
/// layers only know this interface, so they do not depend on ASP.NET Core.
/// </summary>
public interface ILiveUpdatePublisher
{
    Task PublishTelemetryAsync(TelemetryResponse telemetry, CancellationToken cancellationToken);

    Task PublishLinkStatusAsync(VehicleLinkResponse status, CancellationToken cancellationToken);

    /// <summary>Someone took or released control of a vehicle; every operator's screen shows who is in control.</summary>
    Task PublishCommandLeaseAsync(CommandLeaseResponse lease, CancellationToken cancellationToken);
}

/// <summary>
/// Receives link state changes from the MAVLink layer. Must return immediately: it is called from the link's own loops,
/// which must never wait on slow consumers (SignalR clients, the database).
/// </summary>
public interface IVehicleLinkEventSink
{
    void StateChanged(VehicleLinkStatus status, ConnectionState previous);
}

/// <summary>Writes events to the outbox outside of an aggregate save (e.g. link events, which are not persisted state).</summary>
public interface IEventOutbox
{
    Task EnqueueAsync(IReadOnlyCollection<IDomainEvent> events, CancellationToken cancellationToken);
}

/// <summary>One stored telemetry sample (history), flattened for storage and charts.</summary>
public sealed record TelemetrySample(
    VehicleId VehicleId,
    DateTimeOffset RecordedAt,
    GeoPosition? Position,
    AttitudeAngles? Attitude,
    MotionState? Motion,
    BatteryState? Battery,
    GpsState? Gps,
    FlightState? Flight)
{
    public static TelemetrySample From(TelemetrySnapshot snapshot, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new TelemetrySample(snapshot.VehicleId, recordedAt, snapshot.Position, snapshot.Attitude, snapshot.Motion,
            snapshot.Battery, snapshot.Gps, snapshot.Flight);
    }
}

/// <summary>Historical telemetry storage (PostgreSQL). Written in batches, never per message.</summary>
public interface ITelemetryHistoryStore
{
    Task AppendAsync(IReadOnlyCollection<TelemetrySample> samples, CancellationToken cancellationToken);

    /// <summary>Samples in [since, until), oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<TelemetrySample>> QueryAsync(
        VehicleId vehicleId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken);

    /// <returns>Number of samples deleted.</returns>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
