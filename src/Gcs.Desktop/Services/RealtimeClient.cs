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
/// <para>
/// Both hubs are watched: link status and quality arrive on the vehicles hub, so a vehicles hub that dropped while
/// telemetry kept flowing used to freeze every link row on its last value. The state is Connected only while both
/// hubs are, and a hub that SignalR gave up on is started again (the backend may be down for longer than a restart).
/// </para>
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
    private int _starting;

    /// <param name="apiBaseUrl">The API root.</param>
    /// <param name="accessToken">Supplies the bearer token for every (re)connect; SignalR sends it as <c>access_token</c>.</param>
    public RealtimeClient(Uri apiBaseUrl, Func<Task<string?>> accessToken)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUrl);
        _telemetry = Build(new Uri(apiBaseUrl, RealtimeRoutes.TelemetryHub), accessToken);
        _vehicles = Build(new Uri(apiBaseUrl, RealtimeRoutes.VehiclesHub), accessToken);

        _telemetry.On<TelemetryResponse>(nameof(ITelemetryHubClient.TelemetryUpdated), t => TelemetryReceived?.Invoke(t));
        _vehicles.On<VehicleLinkResponse>(nameof(IVehiclesHubClient.LinkStatusChanged), s => LinkStatusReceived?.Invoke(s));

        // A quality update is a complete link status, so it takes the same path (Phase 12).
        _vehicles.On<VehicleLinkResponse>(nameof(IVehiclesHubClient.LinkQualityUpdated), s => LinkStatusReceived?.Invoke(s));
        _vehicles.On<CommandLeaseResponse>(nameof(IVehiclesHubClient.CommandLeaseChanged), l => CommandLeaseReceived?.Invoke(l));

        foreach (var hub in new[] { _telemetry, _vehicles })
        {
            hub.Reconnecting += _ =>
            {
                UpdateState();
                return Task.CompletedTask;
            };
            hub.Reconnected += async _ =>
            {
                if (hub == _telemetry)
                {
                    await ResubscribeAsync();
                }

                UpdateState();
            };
            hub.Closed += _ =>
            {
                UpdateState();
                if (!_stop.IsCancellationRequested)
                {
                    StartInBackground();
                }

                return Task.CompletedTask;
            };
        }
    }

    public event Action<TelemetryResponse>? TelemetryReceived;

    public event Action<VehicleLinkResponse>? LinkStatusReceived;

    public event Action<CommandLeaseResponse>? CommandLeaseReceived;

    public event Action<BackendConnectionState>? ConnectionStateChanged;

    public BackendConnectionState State => _state;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartInBackground();
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

    /// <summary>The state both hubs give together: Connected only when both are, Reconnecting while either is.</summary>
    internal static BackendConnectionState Combine(HubConnectionState telemetry, HubConnectionState vehicles)
    {
        if (telemetry == HubConnectionState.Connected && vehicles == HubConnectionState.Connected)
        {
            return BackendConnectionState.Connected;
        }

        if (telemetry == HubConnectionState.Reconnecting || vehicles == HubConnectionState.Reconnecting)
        {
            return BackendConnectionState.Reconnecting;
        }

        return telemetry == HubConnectionState.Connecting || vehicles == HubConnectionState.Connecting
            ? BackendConnectionState.Connecting
            : BackendConnectionState.Disconnected;
    }

    private static HubConnection Build(Uri url, Func<Task<string?>> accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(url, options => options.AccessTokenProvider = accessToken)
            .WithAutomaticReconnect(new KeepTryingRetryPolicy())
            .Build();

    /// <summary>One start loop at a time; a request while one runs is already covered by it.</summary>
    private void StartInBackground()
    {
        if (Interlocked.Exchange(ref _starting, 1) == 0)
        {
            _ = Task.Run(() => StartWithRetryAsync(_stop.Token), CancellationToken.None);
        }
    }

    private async Task StartWithRetryAsync(CancellationToken stopping)
    {
        SetState(BackendConnectionState.Connecting);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    if (_telemetry.State == HubConnectionState.Disconnected)
                    {
                        await _telemetry.StartAsync(stopping);
                        await ResubscribeAsync();
                    }

                    if (_vehicles.State == HubConnectionState.Disconnected)
                    {
                        await _vehicles.StartAsync(stopping);
                    }

                    UpdateState();
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
                {
                    // Backend not reachable yet (refused, timed out, 502 from a proxy...): try again shortly.
                    await Task.Delay(StartRetryDelay, stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
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

    private void UpdateState() => SetState(Combine(_telemetry.State, _vehicles.State));

    private void SetState(BackendConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        ConnectionStateChanged?.Invoke(state);
    }

    /// <summary>SignalR's default gives up after four attempts (about 42 s); a ground station never should.</summary>
    private sealed class KeepTryingRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < Delays.Length ? Delays[retryContext.PreviousRetryCount] : TimeSpan.FromSeconds(10);
    }
}
