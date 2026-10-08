using System.Collections.Concurrent;
using Gcs.Application.Abstractions;
using Gcs.Domain.Commands;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;
using Gcs.Mavlink.Connections;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Transports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Gcs.UnitTests.Mavlink;

/// <summary>
/// The link lifecycle with a fake clock: timeouts and backoff delays are driven by advancing time,
/// so minutes of simulated silence run in milliseconds.
/// </summary>
/// <remarks>
/// Tests advance the clock <i>until</i> a state is reached (with an upper bound) and then check it did not happen too
/// early. Advancing a fixed amount and asserting immediately is fragile: on a slow CI machine the connection's loops
/// may react a few simulated steps later than on a fast laptop.
/// </remarks>
public sealed class MavlinkConnectionTests : IAsyncDisposable
{
    private const byte VehicleSystemId = 1;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLink _link = new();
    private readonly RecordingSink _sink = new();
    private readonly RecordingEvents _events = new();
    private readonly MavlinkConnectionOptions _options = new()
    {
        HeartbeatTimeoutMilliseconds = 3000,
        ConnectTimeoutMilliseconds = 10_000,
        MaxReconnectAttempts = 3,
        ReconnectBaseDelayMilliseconds = 1000,
        ReconnectMaxDelayMilliseconds = 8000,
        ReconnectJitterRatio = 0,
    };

    private readonly MavlinkConnection _connection;
    private byte _vehicleSequence;

    public MavlinkConnectionTests()
    {
        var target = new VehicleLinkTarget(
            VehicleId.New(), MavlinkSystemId.Create(VehicleSystemId).Value, AutopilotType.Px4, VehicleType.Multirotor,
            ConnectionSettings.Simulator());
        _connection = new MavlinkConnection(
            target, new FixedFactory(_link.GcsSide), _sink, _events, _options, _time, NullLogger.Instance, new Random(1));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_heartbeat_moves_the_link_from_connecting_to_connected()
    {
        _connection.Start();
        _connection.State.ShouldBe(ConnectionState.Connecting);

        await SendFromVehicleAsync(VehicleHeartbeat());

        await EventuallyAsync(() => _connection.State == ConnectionState.Connected);
        _connection.GetStatus().LastHeartbeatAt.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public async Task No_heartbeat_within_the_connect_timeout_faults_the_link()
    {
        _connection.Start();

        var elapsed = await AdvanceUntilAsync(() => _connection.State == ConnectionState.Faulted, TimeSpan.FromSeconds(30));

        elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
        _connection.GetStatus().FaultReason.ShouldBe("No heartbeat within 10 s.");
    }

    [Fact]
    public async Task Heartbeats_from_other_systems_are_ignored()
    {
        _connection.Start();

        await SendFromVehicleAsync(VehicleHeartbeat(), systemId: 42);
        await AdvanceAsync(TimeSpan.FromSeconds(1));

        _connection.State.ShouldBe(ConnectionState.Connecting);
    }

    [Fact]
    public async Task Missing_heartbeats_switch_a_connected_link_to_reconnecting()
    {
        await ConnectAsync();

        var elapsed = await AdvanceUntilAsync(() => _connection.State == ConnectionState.Reconnecting, TimeSpan.FromSeconds(15));

        elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Heartbeat_during_reconnect_restores_the_link_and_resets_attempts()
    {
        await ConnectAsync();
        await AdvanceUntilAsync(() => _connection.GetStatus().ReconnectAttempts == 1, TimeSpan.FromSeconds(30));

        // The vehicle is back. The link is in its backoff wait, so the heartbeat is read when the next attempt opens.
        await SendFromVehicleAsync(VehicleHeartbeat());
        await AdvanceUntilAsync(() => _connection.State == ConnectionState.Connected, TimeSpan.FromSeconds(30));

        _connection.GetStatus().ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task Reconnecting_gives_up_after_the_maximum_attempts()
    {
        await ConnectAsync();

        // Lost after 3 s, then 3 attempts: wait 1 s + 3 s, wait 2 s + 3 s, wait 4 s + 3 s = at least 19 s.
        var elapsed = await AdvanceUntilAsync(() => _connection.State == ConnectionState.Faulted, TimeSpan.FromSeconds(90));

        elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(19));
        var status = _connection.GetStatus();
        status.ReconnectAttempts.ShouldBe(3);
        status.FaultReason.ShouldBe("No heartbeat after 3 reconnect attempts.");
    }

    [Fact]
    public async Task Telemetry_messages_reach_the_sink_in_domain_units()
    {
        await ConnectAsync();

        await SendFromVehicleAsync(new GlobalPositionIntMessage(0, 399250000, 328540000, 950_000, 50_000, 0, 0, 0, 0));

        await EventuallyAsync(() => _sink.Updates.Any(u => u.Position is not null));
        _sink.Updates.First(u => u.Position is not null).Position.ShouldBe(new GeoPosition(39.925, 32.854, 950, 50));
        _sink.Updates.First(u => u.Flight is not null).Flight!.FlightMode.ShouldBe("AUTO.LOITER");
    }

    [Fact]
    public async Task The_gcs_sends_its_own_heartbeat_as_a_ground_station()
    {
        _connection.Start();

        var buffer = new byte[512];
        var read = await _link.VehicleSide.ReceiveAsync(buffer, Ct);
        var frame = new MavlinkFrameParser().Parse(buffer.AsSpan(0, read)).ShouldHaveSingleItem();
        MavlinkCodec.TryDecode(frame, out var message);

        frame.SystemId.ShouldBe((byte)255);
        frame.ComponentId.ShouldBe(MavComponent.MissionPlanner);
        message.ShouldBeOfType<HeartbeatMessage>().Type.ShouldBe(MavType.Gcs);
    }

    [Fact]
    public async Task Link_quality_counts_lost_frames()
    {
        await ConnectAsync();

        _vehicleSequence += 5; // pretend five frames were lost on the way
        await SendFromVehicleAsync(VehicleHeartbeat());

        await EventuallyAsync(() => _connection.GetStatus().Quality.FramesLost == 5);
    }

    [Fact]
    public async Task Every_state_change_is_reported_with_its_previous_state()
    {
        await ConnectAsync();
        await AdvanceUntilAsync(() => _connection.State == ConnectionState.Reconnecting, TimeSpan.FromSeconds(15));
        await _connection.DisposeAsync();

        _events.Transitions.ShouldBe(
        [
            (ConnectionState.Disconnected, ConnectionState.Connecting),
            (ConnectionState.Connecting, ConnectionState.Connected),
            (ConnectionState.Connected, ConnectionState.Reconnecting),
            (ConnectionState.Reconnecting, ConnectionState.Disconnected),
        ]);
    }

    [Fact]
    public async Task A_command_is_not_sent_without_a_connected_link()
    {
        var delivery = await _connection.SendCommandAsync(VehicleCommand.Arm(), Ct);

        delivery.Status.ShouldBe(CommandDeliveryStatus.NotConnected);
    }

    [Fact]
    public async Task The_same_command_is_refused_while_the_first_one_still_waits_for_its_ack()
    {
        await ConnectAsync();

        var first = _connection.SendCommandAsync(VehicleCommand.Arm(), Ct);
        var duplicate = await _connection.SendCommandAsync(VehicleCommand.Arm(), Ct);

        // DISARM is the same MAV_CMD (400) as ARM: its ACK could not be told apart, so it waits its turn too.
        var disarm = await _connection.SendCommandAsync(VehicleCommand.Disarm(), Ct);
        duplicate.Status.ShouldBe(CommandDeliveryStatus.AlreadyInFlight);
        disarm.Status.ShouldBe(CommandDeliveryStatus.AlreadyInFlight);
        first.IsCompleted.ShouldBeFalse();

        await SendFromVehicleAsync(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));
        (await first).ShouldBe(new CommandDelivery(CommandDeliveryStatus.Accepted, 1, null));

        // Once answered, the same command may be sent again.
        var again = _connection.SendCommandAsync(VehicleCommand.Arm(), Ct);
        await SendFromVehicleAsync(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));
        (await again).Status.ShouldBe(CommandDeliveryStatus.Accepted);
    }

    [Fact]
    public async Task A_different_command_is_not_blocked_by_one_in_flight()
    {
        await ConnectAsync();

        var modeChange = _connection.SendCommandAsync(VehicleCommand.SetMode("POSCTL").Value, Ct);
        var rtl = _connection.SendCommandAsync(VehicleCommand.ReturnToLaunch(), Ct);
        await SendFromVehicleAsync(new CommandAckMessage(MavCmd.NavReturnToLaunch, MavResult.Accepted));

        (await rtl).Status.ShouldBe(CommandDeliveryStatus.Accepted);
        modeChange.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unanswered_command_times_out_after_every_retry_has_had_its_full_timeout()
    {
        await ConnectAsync();

        // Keep the link alive (heartbeats) while the autopilot ignores the command.
        var command = _connection.SendCommandAsync(VehicleCommand.ReturnToLaunch(), Ct);
        var elapsed = TimeSpan.Zero;
        while (!command.IsCompleted && elapsed < TimeSpan.FromSeconds(30))
        {
            await SendFromVehicleAsync(VehicleHeartbeat());
            await AdvanceAsync(TimeSpan.FromMilliseconds(500));
            elapsed += TimeSpan.FromMilliseconds(500);
        }

        var delivery = await command;
        delivery.Status.ShouldBe(CommandDeliveryStatus.TimedOut);
        delivery.Attempts.ShouldBe(1 + _options.CommandMaxRetries);
        elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(_options.CommandAckTimeoutMilliseconds * (1 + _options.CommandMaxRetries)));
        _connection.State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task Px4_takeoff_needs_the_home_altitude_from_position_telemetry()
    {
        await ConnectAsync();
        var takeoff = VehicleCommand.Takeoff(30).Value;

        var before = await _connection.SendCommandAsync(takeoff, Ct);
        await SendFromVehicleAsync(new GlobalPositionIntMessage(0, 399250000, 328540000, 938_000, 0, 0, 0, 0, 0));
        await EventuallyAsync(() => _sink.Updates.Any(u => u.Position is not null));
        var after = _connection.SendCommandAsync(takeoff, Ct);
        await SendFromVehicleAsync(new CommandAckMessage(MavCmd.NavTakeoff, MavResult.Accepted));

        before.Status.ShouldBe(CommandDeliveryStatus.Unsupported);
        (await after).Status.ShouldBe(CommandDeliveryStatus.Accepted);
    }

    [Fact]
    public async Task The_gcs_sends_timesync_requests_and_the_echo_gives_the_round_trip()
    {
        await ConnectAsync();

        var request = await ReceiveFromGcsAsync<TimesyncMessage>();
        await AdvanceAsync(TimeSpan.FromMilliseconds(150));
        await SendFromVehicleAsync(new TimesyncMessage(42_000_000_000, request.Ts1));

        request.IsRequest.ShouldBeTrue();
        await EventuallyAsync(() => _connection.GetStatus().Quality.RoundTripMilliseconds is not null);
        _connection.GetStatus().Quality.RoundTripMilliseconds!.Value.ShouldBe(150, tolerance: 1);
    }

    [Fact]
    public async Task Radio_status_is_taken_from_the_radio_although_it_is_another_system()
    {
        await ConnectAsync();

        await SendFromVehicleAsync(new RadioStatusMessage(150, 140, 100, 45, 47, 3, 1), systemId: 51);

        await EventuallyAsync(() => _connection.GetStatus().Quality.Radio is not null);
        _connection.GetStatus().Quality.Radio!.Rssi.ShouldBe(150);
        _connection.State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task A_connected_link_with_fresh_frames_is_graded()
    {
        await ConnectAsync();

        _connection.GetStatus().Quality.Grade.ShouldBe(LinkQualityGrade.Good);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>Reads what the GCS sent until a message of type <typeparamref name="T"/> arrives.</summary>
    private async Task<T> ReceiveFromGcsAsync<T>()
        where T : IMavlinkMessage
    {
        var parser = new MavlinkFrameParser();
        var buffer = new byte[2048];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            var read = await _link.VehicleSide.ReceiveAsync(buffer, timeout.Token);
            foreach (var frame in parser.Parse(buffer.AsSpan(0, read)))
            {
                if (MavlinkCodec.TryDecode(frame, out var message) && message is T wanted)
                {
                    return wanted;
                }
            }
        }
    }

    private async Task ConnectAsync()
    {
        _connection.Start();
        await SendFromVehicleAsync(VehicleHeartbeat());
        await EventuallyAsync(() => _connection.State == ConnectionState.Connected);
    }

    private static HeartbeatMessage VehicleHeartbeat() => new(
        MavType.Quadrotor, MavAutopilot.Px4, MavBaseMode.CustomModeEnabled, (4u << 16) | (3u << 24), MavState.Active);

    private ValueTask SendFromVehicleAsync(IMavlinkMessage message, byte systemId = VehicleSystemId) =>
        _link.VehicleSide.SendAsync(MavlinkCodec.Encode(message, _vehicleSequence++, systemId, MavComponent.Autopilot1), Ct);

    /// <summary>Moves the fake clock forward in small steps, letting the connection's loops react after each step.</summary>
    private async Task AdvanceAsync(TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Step)
        {
            await StepAsync();
        }
    }

    /// <summary>Advances the fake clock until <paramref name="condition"/> holds; returns the simulated time it took.</summary>
    private async Task<TimeSpan> AdvanceUntilAsync(Func<bool> condition, TimeSpan limit)
    {
        var elapsed = TimeSpan.Zero;
        while (!condition())
        {
            if (elapsed > limit)
            {
                throw new TimeoutException($"Condition not met within {limit.TotalSeconds} s of simulated time.");
            }

            await StepAsync();
            elapsed += Step;
        }

        return elapsed;
    }

    private async Task StepAsync()
    {
        _time.Advance(Step);
        await Task.Delay(3, Ct); // real time: give the connection's continuations a chance to run
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within 5 s.");
            }

            await Task.Delay(5, Ct);
        }
    }

    private sealed class FixedFactory(IMavlinkTransport transport) : IMavlinkTransportFactory
    {
        public IMavlinkTransport Create(ConnectionSettings settings, byte systemId) => transport;
    }

    private sealed class RecordingEvents : IVehicleLinkEventSink
    {
        private readonly ConcurrentQueue<(ConnectionState From, ConnectionState To)> _transitions = new();

        public IReadOnlyList<(ConnectionState From, ConnectionState To)> Transitions => [.. _transitions];

        public void StateChanged(VehicleLinkStatus status, ConnectionState previous) => _transitions.Enqueue((previous, status.State));
    }

    private sealed class RecordingSink : ITelemetrySink
    {
        private readonly ConcurrentQueue<TelemetryUpdate> _updates = new();

        public IEnumerable<TelemetryUpdate> Updates => _updates;

        public void Publish(VehicleId vehicleId, TelemetryUpdate update) => _updates.Enqueue(update);
    }
}
