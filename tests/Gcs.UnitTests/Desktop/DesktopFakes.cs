using Gcs.Contracts.Vehicles;
using Gcs.Desktop.Services;

namespace Gcs.UnitTests.Desktop;

internal sealed class FakeApi : IGcsApiClient
{
    public List<VehicleResponse> Vehicles { get; } = [];

    public Dictionary<Guid, string> LinkStates { get; } = [];

    public Dictionary<Guid, TelemetryResponse> Latest { get; } = [];

    public List<(string Action, Guid VehicleId)> Calls { get; } = [];

    public bool Unreachable { get; set; }

    public Task<IReadOnlyList<VehicleResponse>> GetActiveVehiclesAsync(CancellationToken cancellationToken) =>
        Unreachable ? throw new HttpRequestException("connection refused") : Task.FromResult<IReadOnlyList<VehicleResponse>>(Vehicles);

    public Task<VehicleLinkResponse> GetLinkAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        Task.FromResult(Link(vehicleId, LinkStates.GetValueOrDefault(vehicleId, "Disconnected")));

    public Task<TelemetryResponse?> GetLatestTelemetryAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        Task.FromResult(Latest.GetValueOrDefault(vehicleId));

    public Task ConnectAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        Calls.Add(("connect", vehicleId));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        Calls.Add(("disconnect", vehicleId));
        return Task.CompletedTask;
    }

    public static VehicleLinkResponse Link(Guid id, string state) => new(id, state, null, 0, null, new LinkQualityDto(0, 0, 0, 0));
}

internal sealed class FakeRealtime : IRealtimeClient
{
    public event Action<TelemetryResponse>? TelemetryReceived;

    public event Action<VehicleLinkResponse>? LinkStatusReceived;

    public event Action<BackendConnectionState>? ConnectionStateChanged;

    public BackendConnectionState State { get; private set; }

    public List<string> Calls { get; } = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Calls.Add("start");
        return Task.CompletedTask;
    }

    public Task SubscribeVehicleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        Calls.Add($"subscribe {vehicleId}");
        return Task.CompletedTask;
    }

    public Task UnsubscribeVehicleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        Calls.Add($"unsubscribe {vehicleId}");
        return Task.CompletedTask;
    }

    public void RaiseTelemetry(TelemetryResponse telemetry) => TelemetryReceived?.Invoke(telemetry);

    public void RaiseLinkStatus(VehicleLinkResponse status) => LinkStatusReceived?.Invoke(status);

    public void RaiseState(BackendConnectionState state)
    {
        State = state;
        ConnectionStateChanged?.Invoke(state);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Runs "UI thread" work immediately, so tests do not need an Avalonia dispatcher.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal static class DesktopTestData
{
    public static VehicleResponse Vehicle(string callsign) => new(
        Guid.NewGuid(), callsign, 1, "Px4", "Multirotor", new ConnectionSettingsDto("Simulator"), "Active", 1,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public static TelemetryResponse Telemetry(Guid vehicleId, double latitude = 39.9255, int battery = 80) => new(
        vehicleId,
        new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
        new PositionDto(latitude, 32.8663, 1038, 100),
        new AttitudeDto(5.6, 2.0, 117),
        new MotionDto(12, 12.3, -0.4, 117),
        new BatteryDto(16.4, 15, battery),
        new GpsDto("Fix3D", 14),
        new FlightDto(true, "AUTO.LOITER"));
}
