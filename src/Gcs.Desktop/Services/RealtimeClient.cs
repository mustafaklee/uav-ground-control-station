using Gcs.Contracts.Commands;
using Gcs.Contracts.Realtime;
using Gcs.Contracts.Vehicles;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gcs.Desktop.Services;

public enum BackendConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>Live updates from the backend. Events are raised on background threads; view models marshal to the UI thread.</summary>
public interface IRealtimeClient : IAsyncDisposable
{
    event Action<TelemetryResponse>? TelemetryReceived;

    event Action<VehicleLinkResponse>? LinkStatusReceived;

    /// <summary>Someone took or released control of a vehicle.</summary>
    event Action<CommandLeaseResponse>? CommandLeaseReceived;

    event Action<BackendConnectionState>? ConnectionStateChanged;

    BackendConnectionState State { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task SubscribeVehicleAsync(Guid vehicleId, CancellationToken cancellationToken);

    Task UnsubscribeVehicleAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <summary>
/// SignalR connections to /hubs/telemetry and /hubs/vehicles with automatic reconnect. If the backend is not up when
/// the client starts, it keeps retrying in the background instead of failing: operators can open the GCS first.
/// After a reconnect, telemetry subscriptions are restored (SignalR groups do not survive a new connection).
/// </summary>
public sealed class RealtimeClient : IRealtimeClient
{
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(3);

    private readonly HubConnection _telemetry;
    private readonly HubConnection _vehicles;
    private readonly HashSet<Guid> _subscriptions = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private BackendConnectionState _state = BackendConnectionState.Disconnected;

    public RealtimeClient(Uri apiBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUrl);
        _telemetry = Build(new Uri(apiBaseUrl, RealtimeRoutes.TelemetryHub));
        _vehicles = Build(new Uri(apiBaseUrl, RealtimeRoutes.VehiclesHub));

        _telemetry.On<TelemetryResponse>(nameof(ITelemetryHubClient.TelemetryUpdated), t => TelemetryReceived?.Invoke(t));
        _vehicles.On<VehicleLinkResponse>(nameof(IVehiclesHubClient.LinkStatusChanged), s => LinkStatusReceived?.Invoke(s));
        _vehicles.On<CommandLeaseResponse>(nameof(IVehiclesHubClient.CommandLeaseChanged), l => CommandLeaseReceived?.Invoke(l));

        _telemetry.Reconnecting += _ =>
        {
            SetState(BackendConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _telemetry.Reconnected += async _ =>
        {
            await ResubscribeAsync();
            SetState(BackendConnectionState.Connected);
        };
        _telemetry.Closed += _ =>
        {
            SetState(BackendConnectionState.Disconnected);
            return Task.CompletedTask;
        };
    }

    public event Action<TelemetryResponse>? TelemetryReceived;

    public event Action<VehicleLinkResponse>? LinkStatusReceived;

    public event Action<CommandLeaseResponse>? CommandLeaseReceived;

    public event Action<BackendConnectionState>? ConnectionStateChanged;

    public BackendConnectionState State => _state;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => StartWithRetryAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task SubscribeVehicleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _subscriptions.Add(vehicleId);
        }

        if (_telemetry.State == HubConnectionState.Connected)
        {
            await _telemetry.InvokeAsync(TelemetryHubMethods.SubscribeVehicle, vehicleId, cancellationToken);
        }
    }

    public async Task UnsubscribeVehicleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _subscriptions.Remove(vehicleId);
        }

        if (_telemetry.State == HubConnectionState.Connected)
        {
            await _telemetry.InvokeAsync(TelemetryHubMethods.UnsubscribeVehicle, vehicleId, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _telemetry.DisposeAsync();
        await _vehicles.DisposeAsync();
        _stop.Dispose();
    }

    private static HubConnection Build(Uri url) =>
        new HubConnectionBuilder().WithUrl(url).WithAutomaticReconnect().Build();

    private async Task StartWithRetryAsync(CancellationToken stopping)
    {
        SetState(BackendConnectionState.Connecting);
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (_telemetry.State == HubConnectionState.Disconnected)
                {
                    await _telemetry.StartAsync(stopping);
                }

                if (_vehicles.State == HubConnectionState.Disconnected)
                {
                    await _vehicles.StartAsync(stopping);
                }

                await ResubscribeAsync();
                SetState(BackendConnectionState.Connected);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
            {
                // Backend not reachable yet: try again shortly.
                await Task.Delay(StartRetryDelay, stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task ResubscribeAsync()
    {
        Guid[] vehicles;
        lock (_gate)
        {
            vehicles = [.. _subscriptions];
        }

        foreach (var vehicleId in vehicles)
        {
            await _telemetry.InvokeAsync(TelemetryHubMethods.SubscribeVehicle, vehicleId, _stop.Token);
        }
    }

    private void SetState(BackendConnectionState state)
    {
        _state = state;
        ConnectionStateChanged?.Invoke(state);
    }
}
