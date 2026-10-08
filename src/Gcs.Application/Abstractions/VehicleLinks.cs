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

/// <summary>
/// Link quality (ADR-019). Totals since the link started come from sequence numbers and checksums; the recent values cover
/// the last few seconds, which is what an operator indicator needs.
/// </summary>
/// <param name="FramesReceived">Frames decoded since the link started.</param>
/// <param name="FramesLost">Frames inferred lost from sequence gaps since the link started.</param>
/// <param name="PacketLossRatio">Lost / (arrived + lost) since the link started.</param>
/// <param name="CrcErrors">Frames dropped for a bad checksum since the link started.</param>
/// <param name="RecentPacketLossRatio">Lost / (arrived + lost) over the recent window.</param>
/// <param name="MessagesPerSecond">Frames per second over the recent window.</param>
/// <param name="RoundTripMilliseconds">Smoothed TIMESYNC round trip; null until the vehicle answered one.</param>
/// <param name="LastFrameAt">When the last frame arrived; null before the first.</param>
/// <param name="Grade">The at-a-glance verdict (<see cref="LinkQualityRules"/>).</param>
/// <param name="Radio">The telemetry radio's own report, when the link has a MAVLink radio.</param>
public sealed record LinkQuality(
    long FramesReceived,
    long FramesLost,
    double PacketLossRatio,
    long CrcErrors,
    double RecentPacketLossRatio = 0,
    double MessagesPerSecond = 0,
    double? RoundTripMilliseconds = null,
    DateTimeOffset? LastFrameAt = null,
    LinkQualityGrade Grade = LinkQualityGrade.Lost,
    RadioLinkStatus? Radio = null);

/// <summary>
/// RADIO_STATUS from a telemetry radio: signal strength and noise at this end and the remote end (radio units, SiK:
/// 0-255), receive errors and corrected packets, and how full the radio's transmit buffer is.
/// </summary>
public sealed record RadioLinkStatus(
    int Rssi,
    int RemoteRssi,
    int Noise,
    int RemoteNoise,
    int ReceiveErrors,
    int Corrected,
    int TxBufferPercent,
    DateTimeOffset ReceivedAt);

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

    /// <summary>Every link this process manages (for health checks and metrics).</summary>
    IReadOnlyList<VehicleLinkStatus> GetAll();
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
