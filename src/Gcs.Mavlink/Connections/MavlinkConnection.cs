using System.Collections.Concurrent;
using System.Threading.Channels;
using Gcs.Application.Abstractions;
using Gcs.Domain.Commands;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles.Connections;
using Gcs.Mavlink.Commands;
using Gcs.Mavlink.Missions;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Transports;
using Gcs.Mavlink.Translation;
using Microsoft.Extensions.Logging;

namespace Gcs.Mavlink.Connections;

/// <summary>
/// The live link to one vehicle. Runs a session loop:
/// <code>
/// open transport ─► receive loop (frames → telemetry, heartbeats)
///                ├► heartbeat loop (GCS HEARTBEAT at 1 Hz)
///                └► watchdog (connect timeout / heartbeat timeout)
/// session ends ─► Connecting: fault │ Connected: Reconnecting │ Reconnecting: count attempt
///             ─► wait (exponential backoff) ─► open a new session ... until connected again or Faulted
/// </code>
/// State transitions go through the domain <see cref="VehicleConnection"/> state machine, guarded by a lock because
/// the receive loop, the watchdog and API requests (status, disconnect) touch it from different threads.
/// </summary>
internal sealed partial class MavlinkConnection : IAsyncDisposable
{
    private const int ReceiveBufferSize = 4096;

    private readonly VehicleLinkTarget _target;
    private readonly IMavlinkTransportFactory _transports;
    private readonly ITelemetrySink _telemetry;
    private readonly IVehicleLinkEventSink _events;
    private readonly MavlinkConnectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Random _random;
    private readonly Lock _gate = new();
    private readonly VehicleConnection _state;
    private readonly MavlinkFrameParser _parser = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _missionGate = new(1, 1);

    // One inbox per command that is waiting for its COMMAND_ACK. The key is MAV_CMD because that is all an ACK carries:
    // two identical commands in flight at once could not be told apart, so a second one is refused (see SendCommandAsync).
    private readonly ConcurrentDictionary<MavCmd, Channel<CommandAckMessage>> _pendingCommands = new();

    private Task? _run;
    private volatile IMavlinkTransport? _activeTransport;
    private volatile Channel<IMavlinkMessage>? _missionInbox;
    private DateTimeOffset _sessionStartedAt;
    private int _sequence;
    private int _disposed;
    private double _homeAltitudeMsl = double.NaN;

    public MavlinkConnection(
        VehicleLinkTarget target,
        IMavlinkTransportFactory transports,
        ITelemetrySink telemetry,
        IVehicleLinkEventSink events,
        MavlinkConnectionOptions options,
        TimeProvider time,
        ILogger logger,
        Random? random = null)
    {
        _target = target;
        _transports = transports;
        _telemetry = telemetry;
        _events = events;
        _options = options;
        _time = time;
        _logger = logger;
        _random = random ?? Random.Shared;
        _state = new VehicleConnection(target.VehicleId, options.MaxReconnectAttempts);
    }

    public VehicleLinkTarget Target => _target;

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state.State;
            }
        }
    }

    public VehicleLinkStatus GetStatus()
    {
        lock (_gate)
        {
            return BuildStatus();
        }
    }

    /// <summary>Starts the background session loop. Returns immediately.</summary>
    public void Start()
    {
        var begin = Mutate(state => state.BeginConnect());
        if (!begin.IsSuccess)
        {
            throw new InvalidOperationException(begin.Error.Message);
        }

        _run = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Stops the link. Safe to call more than once (e.g. operator disconnect racing with shutdown).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run;
        }

        Mutate(state => state.Disconnect());
        _stop.Dispose();
        _missionGate.Dispose();
        LogDisconnected(_target.VehicleId.Value);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var reason = await RunSessionAsync(stopping);
            if (stopping.IsCancellationRequested || !OnSessionEnded(reason))
            {
                return;
            }

            int attempt;
            lock (_gate)
            {
                attempt = _state.ReconnectAttempts;
            }

            var delay = ReconnectBackoff.Delay(
                attempt,
                TimeSpan.FromMilliseconds(_options.ReconnectBaseDelayMilliseconds),
                TimeSpan.FromMilliseconds(_options.ReconnectMaxDelayMilliseconds),
                _options.ReconnectJitterRatio,
                _random);
            LogReconnecting(_target.VehicleId.Value, attempt + 1, _options.MaxReconnectAttempts, delay.TotalMilliseconds);

            try
            {
                await Task.Delay(delay, _time, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One transport lifetime. Returns why it ended.</summary>
    private async Task<string> RunSessionAsync(CancellationToken stopping)
    {
        var transport = _transports.Create(_target.Connection, _target.SystemId.Value);
        await using (transport)
        {
            try
            {
                await transport.OpenAsync(stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogOpenFailed(ex, _target.VehicleId.Value, transport.Description);
                return $"Could not open {transport.Description}: {ex.Message}";
            }

            LogSessionStarted(_target.VehicleId.Value, transport.Description);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            lock (_gate)
            {
                _sessionStartedAt = _time.GetUtcNow();
            }

            _activeTransport = transport;
            var receive = ReceiveLoopAsync(transport, session.Token);
            var heartbeat = HeartbeatLoopAsync(transport, session.Token);
            var reason = await WatchdogAsync(receive, session.Token);

            _activeTransport = null;
            await session.CancelAsync();
            await Task.WhenAll(Quietly(receive), Quietly(heartbeat));
            return reason;
        }
    }

    /// <summary>Decides what a lost session means. Returns false when the loop should stop (Faulted).</summary>
    private bool OnSessionEnded(string reason) => Mutate(state =>
    {
        switch (state.State)
        {
            case ConnectionState.Connecting:
                state.Fault(reason);
                LogFaulted(_target.VehicleId.Value, reason);
                return false;
            case ConnectionState.Connected:
                state.HeartbeatLost();
                LogLinkLost(_target.VehicleId.Value, reason);
                return true;
            case ConnectionState.Reconnecting:
                state.ReconnectAttemptFailed();
                if (state.State == ConnectionState.Faulted)
                {
                    LogFaulted(_target.VehicleId.Value, state.FaultReason ?? reason);
                    return false;
                }

                return true;
            default:
                return false;
        }
    });

    private async Task<string> WatchdogAsync(Task receive, CancellationToken session)
    {
        var interval = TimeSpan.FromMilliseconds(_options.WatchdogIntervalMilliseconds);
        var heartbeatTimeout = TimeSpan.FromMilliseconds(_options.HeartbeatTimeoutMilliseconds);
        var connectTimeout = TimeSpan.FromMilliseconds(_options.ConnectTimeoutMilliseconds);

        while (!session.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _time, session);
            }
            catch (OperationCanceledException)
            {
                return "Stopped.";
            }

            if (receive.IsFaulted)
            {
                return $"Receive failed: {receive.Exception?.GetBaseException().Message}";
            }

            var now = _time.GetUtcNow();
            lock (_gate)
            {
                var sinceSessionStart = now - _sessionStartedAt;
                switch (_state.State)
                {
                    case ConnectionState.Connecting when sinceSessionStart > connectTimeout:
                        return $"No heartbeat within {connectTimeout.TotalSeconds:0.#} s.";
                    case ConnectionState.Reconnecting when sinceSessionStart > heartbeatTimeout:
                        return $"No heartbeat within {heartbeatTimeout.TotalSeconds:0.#} s after reconnecting.";
                    case ConnectionState.Connected when now - (_state.LastHeartbeatAt ?? _sessionStartedAt) > heartbeatTimeout:
                        return $"No heartbeat for {heartbeatTimeout.TotalSeconds:0.#} s.";
                    default:
                        break;
                }
            }
        }

        return "Stopped.";
    }

    private async Task ReceiveLoopAsync(IMavlinkTransport transport, CancellationToken session)
    {
        var buffer = new byte[ReceiveBufferSize];
        while (!session.IsCancellationRequested)
        {
            var read = await transport.ReceiveAsync(buffer, session);
            IReadOnlyList<MavlinkFrame> frames;
            lock (_gate)
            {
                frames = _parser.Parse(buffer.AsSpan(0, read));
            }

            foreach (var frame in frames)
            {
                // Ignore other vehicles on a shared link and other GCS instances; only our vehicle's frames count.
                if (frame.SystemId != _target.SystemId.Value || !MavlinkCodec.TryDecode(frame, out var message))
                {
                    continue;
                }

                Handle(message!);
            }
        }
    }

    private void Handle(IMavlinkMessage message)
    {
        if (message is MissionRequestIntMessage or MissionCountMessage or MissionItemIntMessage or MissionAckMessage)
        {
            // Mission protocol replies belong to the transfer in progress, if any; otherwise they are stale and dropped.
            _missionInbox?.Writer.TryWrite(message);
            return;
        }

        if (message is CommandAckMessage ack)
        {
            // An ACK nobody waits for (late answer after a timeout, or for another GCS) is dropped.
            if (_pendingCommands.TryGetValue(ack.Command, out var waiting))
            {
                waiting.Writer.TryWrite(ack);
            }

            return;
        }

        if (message is GlobalPositionIntMessage position)
        {
            // MSL altitude minus altitude above home = home altitude; PX4 takeoff commands need it.
            Volatile.Write(ref _homeAltitudeMsl, (position.AltitudeMslMillimetres - position.RelativeAltitudeMillimetres) / 1000.0);
        }

        var now = _time.GetUtcNow();
        if (message is HeartbeatMessage { Type: not MavType.Gcs })
        {
            var becameConnected = Mutate(state =>
            {
                var previous = state.State;
                state.HeartbeatReceived(now);
                return previous != ConnectionState.Connected && state.State == ConnectionState.Connected;
            });

            if (becameConnected)
            {
                LogConnected(_target.VehicleId.Value);
            }
        }

        if (TelemetryTranslator.Translate(message, now) is { } update)
        {
            _telemetry.Publish(_target.VehicleId, update);
        }
    }

    private async Task HeartbeatLoopAsync(IMavlinkTransport transport, CancellationToken session)
    {
        var heartbeat = new HeartbeatMessage(MavType.Gcs, MavAutopilot.Invalid, MavBaseMode.None, 0, MavState.Active);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMilliseconds), _time);
        do
        {
            var frame = MavlinkCodec.Encode(heartbeat, NextSequence(), (byte)_options.GcsSystemId, MavComponent.MissionPlanner);
            try
            {
                await transport.SendAsync(frame, session);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed send is not fatal by itself; the watchdog decides from incoming heartbeats.
                LogSendFailed(ex, _target.VehicleId.Value);
            }
        }
        while (await timer.WaitForNextTickAsync(session));
    }

    private byte NextSequence() => (byte)Interlocked.Increment(ref _sequence);

    public static readonly Error NotConnected = Error.Conflict(
        "vehicle.link.not_connected", "The vehicle is not connected. Connect it and wait for its heartbeat first.");

    public static readonly Error TransferInProgress = Error.Conflict(
        "vehicle.mission.transfer_in_progress",
        "Another mission transfer to this vehicle is in progress. Wait for it to finish and try again.");

    public Task<Result> UploadMissionAsync(IReadOnlyList<MissionItemIntMessage> items, CancellationToken cancellationToken) =>
        RunMissionTransferAsync(transfer => transfer.UploadAsync(items, cancellationToken), cancellationToken);

    public async Task<Result<IReadOnlyList<MissionItemIntMessage>>> DownloadMissionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MissionItemIntMessage> items = [];
        var result = await RunMissionTransferAsync(
            async transfer =>
            {
                var download = await transfer.DownloadAsync(cancellationToken);
                if (!download.IsSuccess)
                {
                    return download.Error;
                }

                items = download.Value;
                return Result.Success();
            },
            cancellationToken);
        return result.IsSuccess ? Result.Success(items) : result.Error;
    }

    /// <summary>
    /// One transfer at a time per vehicle: two operators uploading at once would interleave MISSION_ITEMs and
    /// corrupt the vehicle's mission, so the second one is refused instead of queued.
    /// </summary>
    /// <summary>
    /// Sends one command and waits for the vehicle's answer (with retries, see <see cref="CommandExchange"/>).
    /// Refused without sending when the link is down, or when the same MAV_CMD is still waiting for its ACK: an operator
    /// double-clicking ARM must produce one ARM, and the second click is told so instead of being queued.
    /// Different commands may overlap (an RTL is not blocked by a mode change still in flight).
    /// </summary>
    public async Task<CommandDelivery> SendCommandAsync(VehicleCommand command, CancellationToken cancellationToken)
    {
        if (State != ConnectionState.Connected || _activeTransport is null)
        {
            return new CommandDelivery(CommandDeliveryStatus.NotConnected, 0, null);
        }

        var home = Volatile.Read(ref _homeAltitudeMsl);
        var message = CommandMapper.ToCommandLong(
            command, _target.Autopilot, _target.Type, _target.SystemId.Value, double.IsNaN(home) ? null : home, out var problem);
        if (message is null)
        {
            return new CommandDelivery(CommandDeliveryStatus.Unsupported, 0, problem);
        }

        var inbox = Channel.CreateUnbounded<CommandAckMessage>();
        if (!_pendingCommands.TryAdd(message.Command, inbox))
        {
            return new CommandDelivery(CommandDeliveryStatus.AlreadyInFlight, 0, null);
        }

        try
        {
            var exchange = new CommandExchange(SendAsync, inbox.Reader, CommandOptions(), _time);
            var delivery = await exchange.RunAsync(message, cancellationToken);
            LogCommand(_target.VehicleId.Value, command.Type, delivery.Status, delivery.Attempts);
            return delivery;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The transport went away while sending (link lost mid-command).
            return new CommandDelivery(CommandDeliveryStatus.NotConnected, 0, $"The link dropped while sending: {ex.Message}");
        }
        finally
        {
            _pendingCommands.TryRemove(message.Command, out _);
        }
    }

    private CommandExchangeOptions CommandOptions() => new()
    {
        AckTimeout = TimeSpan.FromMilliseconds(_options.CommandAckTimeoutMilliseconds),
        MaxRetries = _options.CommandMaxRetries,
        InProgressTimeout = TimeSpan.FromMilliseconds(_options.CommandInProgressTimeoutMilliseconds),
    };

    private async Task<Result> RunMissionTransferAsync(Func<MissionTransfer, Task<Result>> run, CancellationToken cancellationToken)
    {
        if (State != ConnectionState.Connected || _activeTransport is null)
        {
            return NotConnected;
        }

        if (!await _missionGate.WaitAsync(0, cancellationToken))
        {
            return TransferInProgress;
        }

        try
        {
            var inbox = Channel.CreateUnbounded<IMavlinkMessage>();
            _missionInbox = inbox;
            var transfer = new MissionTransfer(
                SendAsync, inbox.Reader, new MissionTransferOptions(), _time, _target.SystemId.Value, MavComponent.Autopilot1);
            return await run(transfer);
        }
        finally
        {
            _missionInbox = null;
            _missionGate.Release();
        }
    }

    private async ValueTask SendAsync(IMavlinkMessage message, CancellationToken cancellationToken)
    {
        var transport = _activeTransport ?? throw new InvalidOperationException("The link has no active transport.");
        var frame = MavlinkCodec.Encode(message, NextSequence(), (byte)_options.GcsSystemId, MavComponent.MissionPlanner);
        await transport.SendAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Runs a state change under the lock and, if the state actually changed, reports it after releasing the lock.
    /// The event sink only enqueues, but calling out while holding a lock is avoided on principle.
    /// </summary>
    private T Mutate<T>(Func<VehicleConnection, T> change)
    {
        ConnectionState previous;
        VehicleLinkStatus? changed = null;
        T result;
        lock (_gate)
        {
            previous = _state.State;
            result = change(_state);
            if (_state.State != previous)
            {
                changed = BuildStatus();
            }
        }

        if (changed is not null)
        {
            _events.StateChanged(changed, previous);
        }

        return result;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private VehicleLinkStatus BuildStatus()
    {
        var stats = _parser.Statistics;
        return new VehicleLinkStatus(
            _target.VehicleId,
            _state.State,
            _state.LastHeartbeatAt,
            _state.ReconnectAttempts,
            _state.FaultReason,
            new LinkQuality(stats.FramesReceived, stats.FramesLost, stats.PacketLossRatio, stats.CrcErrors));
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // Expected while a session is being torn down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vehicle {VehicleId}: link session started on {Transport}")]
    private partial void LogSessionStarted(Guid vehicleId, string transport);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vehicle {VehicleId}: connected (heartbeat received)")]
    private partial void LogConnected(Guid vehicleId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vehicle {VehicleId}: link lost ({Reason})")]
    private partial void LogLinkLost(Guid vehicleId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vehicle {VehicleId}: reconnect attempt {Attempt}/{MaxAttempts} in {DelayMs:0} ms")]
    private partial void LogReconnecting(Guid vehicleId, int attempt, int maxAttempts, double delayMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vehicle {VehicleId}: link faulted ({Reason})")]
    private partial void LogFaulted(Guid vehicleId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vehicle {VehicleId}: could not open {Transport}")]
    private partial void LogOpenFailed(Exception exception, Guid vehicleId, string transport);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Vehicle {VehicleId}: sending heartbeat failed")]
    private partial void LogSendFailed(Exception exception, Guid vehicleId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vehicle {VehicleId}: command {Command} -> {Status} after {Attempts} attempt(s)")]
    private partial void LogCommand(Guid vehicleId, VehicleCommandType command, CommandDeliveryStatus status, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vehicle {VehicleId}: disconnected")]
    private partial void LogDisconnected(Guid vehicleId);
}
