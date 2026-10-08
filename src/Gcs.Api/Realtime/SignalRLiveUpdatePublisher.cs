using Gcs.Application.Abstractions;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Realtime;
using Gcs.Contracts.Vehicles;
using Microsoft.AspNetCore.SignalR;

namespace Gcs.Api.Realtime;

/// <summary>Implements the application's live-update port with SignalR hub contexts.</summary>
internal sealed class SignalRLiveUpdatePublisher(
    IHubContext<TelemetryHub, ITelemetryHubClient> telemetryHub,
    IHubContext<VehiclesHub, IVehiclesHubClient> vehiclesHub) : ILiveUpdatePublisher
{
    public Task PublishTelemetryAsync(TelemetryResponse telemetry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        return telemetryHub.Clients.Group(TelemetryHub.GroupFor(telemetry.VehicleId)).TelemetryUpdated(telemetry);
    }

    public Task PublishLinkStatusAsync(VehicleLinkResponse status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        return vehiclesHub.Clients.All.LinkStatusChanged(status);
    }

    public Task PublishLinkQualityAsync(VehicleLinkResponse status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        return vehiclesHub.Clients.All.LinkQualityUpdated(status);
    }

    public Task PublishCommandLeaseAsync(CommandLeaseResponse lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return vehiclesHub.Clients.All.CommandLeaseChanged(lease);
    }
}
