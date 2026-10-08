using Gcs.Contracts.Commands;
using Gcs.Contracts.Vehicles;

namespace Gcs.Contracts.Realtime;

/// <summary>
/// SignalR endpoints and message names, shared by the API and its clients so both sides use the same strings.
/// REST stays the way to change things; SignalR only pushes state to clients.
/// </summary>
public static class RealtimeRoutes
{
    /// <summary>Live telemetry. Clients subscribe per vehicle and receive updates at a throttled rate (default 5 Hz).</summary>
    public const string TelemetryHub = "/hubs/telemetry";

    /// <summary>Fleet-wide vehicle events: link state, link quality and command lease changes for every vehicle.</summary>
    public const string VehiclesHub = "/hubs/vehicles";
}

/// <summary>Methods a client can call on <see cref="RealtimeRoutes.TelemetryHub"/>.</summary>
public static class TelemetryHubMethods
{
    /// <summary>Start receiving <see cref="ITelemetryHubClient.TelemetryUpdated"/> for one vehicle. Argument: vehicle id (Guid).</summary>
    public const string SubscribeVehicle = "SubscribeVehicle";

    public const string UnsubscribeVehicle = "UnsubscribeVehicle";
}

/// <summary>Messages the server sends on <see cref="RealtimeRoutes.TelemetryHub"/>.</summary>
public interface ITelemetryHubClient
{
    Task TelemetryUpdated(TelemetryResponse telemetry);
}

/// <summary>Messages the server sends on <see cref="RealtimeRoutes.VehiclesHub"/>.</summary>
public interface IVehiclesHubClient
{
    Task LinkStatusChanged(VehicleLinkResponse status);

    /// <summary>Every 2 s for each active link: the same status with fresh link quality (Phase 12).</summary>
    Task LinkQualityUpdated(VehicleLinkResponse status);

    /// <summary>An operator took or released control of a vehicle.</summary>
    Task CommandLeaseChanged(CommandLeaseResponse lease);
}
