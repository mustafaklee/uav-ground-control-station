using Gcs.Domain.Common;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;

namespace Gcs.Application.Abstractions;

/// <summary>What the link layer needs to know to talk to one vehicle.</summary>
public sealed record VehicleLinkTarget(
    VehicleId VehicleId,
    MavlinkSystemId SystemId,
    AutopilotType Autopilot,
    VehicleType Type,
    ConnectionSettings Connection);

/// <summary>Link quality derived from the protocol's sequence numbers and checksums.</summary>
public sealed record LinkQuality(long FramesReceived, long FramesLost, double PacketLossRatio, long CrcErrors);

public sealed record VehicleLinkStatus(
    VehicleId VehicleId,
    ConnectionState State,
    DateTimeOffset? LastHeartbeatAt,
    int ReconnectAttempts,
    string? FaultReason,
    LinkQuality Quality);

/// <summary>
/// Manages live links to vehicles. Implemented by Gcs.Mavlink; the application layer never sees transports or packets,
/// so a different protocol or a radio modem could be plugged in without touching use cases.
/// </summary>
public interface IVehicleLinkManager
{
    /// <summary>Starts connecting in the background and returns immediately; follow progress with <see cref="GetStatus"/>.</summary>
    Task<Result> ConnectAsync(VehicleLinkTarget target, CancellationToken cancellationToken);

    /// <summary>Ends the link. Disconnecting a vehicle that is not connected succeeds.</summary>
    Task DisconnectAsync(VehicleId vehicleId, CancellationToken cancellationToken);

    /// <summary>Current link status, or null when no link was ever requested for the vehicle.</summary>
    VehicleLinkStatus? GetStatus(VehicleId vehicleId);
}

/// <summary>Where the link layer delivers decoded telemetry.</summary>
public interface ITelemetrySink
{
    void Publish(VehicleId vehicleId, TelemetryUpdate update);
}

/// <summary>Read access to the latest telemetry of each vehicle.</summary>
public interface ITelemetryService
{
    TelemetrySnapshot? GetLatest(VehicleId vehicleId);
}
