using Gcs.Application.Abstractions;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Realtime;
using Gcs.Domain.Vehicles;
using Microsoft.AspNetCore.SignalR;

namespace Gcs.Api.Realtime;

/// <summary>
/// Live telemetry hub. Each vehicle has a SignalR group; a client joins the groups of the vehicles it displays,
/// so a screen showing one UAV does not receive (and pay bandwidth for) the whole fleet's telemetry.
/// </summary>
public sealed class TelemetryHub(ITelemetryService telemetry) : Hub<ITelemetryHubClient>
{
    internal static string GroupFor(Guid vehicleId) => $"vehicle:{vehicleId:N}";

    [HubMethodName(TelemetryHubMethods.SubscribeVehicle)]
    public async Task SubscribeVehicleAsync(Guid vehicleId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(vehicleId), Context.ConnectionAborted);

        // Send the current state right away, so the screen is not empty until the next change.
        if (telemetry.GetLatest(new VehicleId(vehicleId)) is { } snapshot)
        {
            await Clients.Caller.TelemetryUpdated(TelemetryMapping.ToResponse(snapshot));
        }
    }

    [HubMethodName(TelemetryHubMethods.UnsubscribeVehicle)]
    public Task UnsubscribeVehicleAsync(Guid vehicleId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(vehicleId), Context.ConnectionAborted);
}

/// <summary>Fleet-wide events (link state changes). Every connected client receives them.</summary>
public sealed class VehiclesHub : Hub<IVehiclesHubClient>;
