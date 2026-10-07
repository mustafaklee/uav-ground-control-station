using Gcs.Application.Abstractions;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;

namespace Gcs.Application.Vehicles;

/// <summary>Use case: open the live link to a registered vehicle using its stored connection settings.</summary>
public sealed class ConnectVehicleHandler(IVehicleRepository vehicles, IVehicleLinkManager links)
{
    public async Task<Result<VehicleLinkResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.GetByIdAsync(new VehicleId(id), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        if (vehicle.IsRetired)
        {
            return VehicleErrors.Retired;
        }

        var target = new VehicleLinkTarget(vehicle.Id, vehicle.SystemId, vehicle.Autopilot, vehicle.Type, vehicle.Connection);
        var connect = await links.ConnectAsync(target, cancellationToken);
        if (!connect.IsSuccess)
        {
            return connect.Error;
        }

        return VehicleLinkMapping.ToResponse(vehicle.Id, links.GetStatus(vehicle.Id));
    }
}

/// <summary>Use case: close the live link. Idempotent.</summary>
public sealed class DisconnectVehicleHandler(IVehicleQueries queries, IVehicleLinkManager links)
{
    public async Task<Result> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await queries.GetByIdAsync(id, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        await links.DisconnectAsync(new VehicleId(id), cancellationToken);
        return Result.Success();
    }
}

/// <summary>Use case: read the link status. A vehicle that was never connected reports Disconnected.</summary>
public sealed class GetVehicleLinkHandler(IVehicleQueries queries, IVehicleLinkManager links)
{
    public async Task<Result<VehicleLinkResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await queries.GetByIdAsync(id, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        var vehicleId = new VehicleId(id);
        return VehicleLinkMapping.ToResponse(vehicleId, links.GetStatus(vehicleId));
    }
}

/// <summary>Use case: read the latest live telemetry of a vehicle.</summary>
public sealed class GetLatestTelemetryHandler(IVehicleQueries queries, ITelemetryService telemetry)
{
    public static readonly Error NotAvailable = Error.NotFound(
        "telemetry.not_available", "No telemetry has been received from this vehicle yet.");

    public async Task<Result<TelemetryResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await queries.GetByIdAsync(id, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        var snapshot = telemetry.GetLatest(new VehicleId(id));
        return snapshot is null ? NotAvailable : VehicleLinkMapping.ToResponse(snapshot);
    }
}

internal static class VehicleLinkMapping
{
    public static VehicleLinkResponse ToResponse(VehicleId vehicleId, VehicleLinkStatus? status) =>
        status is null
            ? new VehicleLinkResponse(vehicleId.Value, nameof(ConnectionState.Disconnected), null, 0, null, new LinkQualityDto(0, 0, 0, 0))
            : new VehicleLinkResponse(
                vehicleId.Value,
                status.State.ToString(),
                status.LastHeartbeatAt,
                status.ReconnectAttempts,
                status.FaultReason,
                new LinkQualityDto(status.Quality.FramesReceived, status.Quality.FramesLost, status.Quality.PacketLossRatio, status.Quality.CrcErrors));

    public static TelemetryResponse ToResponse(TelemetrySnapshot s) => new(
        s.VehicleId.Value,
        s.UpdatedAt,
        s.Position is { } p ? new PositionDto(p.Latitude, p.Longitude, p.AltitudeMsl, p.RelativeAltitude) : null,
        s.Attitude is { } a ? new AttitudeDto(a.Roll, a.Pitch, a.Yaw) : null,
        s.Motion is { } m ? new MotionDto(m.GroundSpeed, m.AirSpeed, m.ClimbRate, m.Heading) : null,
        s.Battery is { } b ? new BatteryDto(b.Voltage, b.Current, b.RemainingPercent) : null,
        s.Gps is { } g ? new GpsDto(g.Fix.ToString(), g.SatellitesVisible) : null,
        s.Flight is { } f ? new FlightDto(f.Armed, f.FlightMode) : null);
}
