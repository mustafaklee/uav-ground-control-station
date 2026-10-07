using Gcs.Application.Abstractions;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;

namespace Gcs.Application.Vehicles;

/// <summary>
/// Use case: open the live link to a registered vehicle using its stored connection settings, and remember that the
/// operator wants it connected (restored after a restart).
/// </summary>
public sealed class ConnectVehicleHandler(IVehicleRepository vehicles, IUnitOfWork unitOfWork, IVehicleLinkManager links)
{
    public async Task<Result<VehicleLinkResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.GetByIdAsync(new VehicleId(id), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        var request = vehicle.RequestLink();
        if (!request.IsSuccess)
        {
            return request.Error;
        }

        var connect = await links.ConnectAsync(VehicleLinkMapping.ToTarget(vehicle), cancellationToken);
        if (!connect.IsSuccess)
        {
            return connect.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return VehicleLinkMapping.ToResponse(vehicle.Id, links.GetStatus(vehicle.Id));
    }
}

/// <summary>Use case: close the live link on purpose (it will not be restored after a restart). Idempotent.</summary>
public sealed class DisconnectVehicleHandler(IVehicleRepository vehicles, IUnitOfWork unitOfWork, IVehicleLinkManager links)
{
    public async Task<Result> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.GetByIdAsync(new VehicleId(id), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        vehicle.ReleaseLink();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await links.DisconnectAsync(vehicle.Id, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// Use case: at startup, reconnect every active vehicle the operator had left connected. Without this, an API restart
/// (deployment, crash, host reboot) silently leaves all vehicles unmonitored until someone notices and reconnects.
/// </summary>
public sealed class RestoreVehicleLinksHandler(IVehicleRepository vehicles, IVehicleLinkManager links)
{
    /// <returns>Number of links restored.</returns>
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var vehicle in await vehicles.ListLinkRequestedAsync(cancellationToken))
        {
            if ((await links.ConnectAsync(VehicleLinkMapping.ToTarget(vehicle), cancellationToken)).IsSuccess)
            {
                restored++;
            }
        }

        return restored;
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
        return snapshot is null ? NotAvailable : TelemetryMapping.ToResponse(snapshot);
    }
}

public static class VehicleLinkMapping
{
    public static VehicleLinkTarget ToTarget(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return new VehicleLinkTarget(vehicle.Id, vehicle.SystemId, vehicle.Autopilot, vehicle.Type, vehicle.Connection);
    }

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
}
