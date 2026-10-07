using System.Collections.Concurrent;
using Gcs.Application.Abstractions;
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
/// so minutes of simulated silence run in milliseconds and the result does not depend on machine speed.
/// </summary>
public sealed class MavlinkConnectionTests : IAsyncDisposable
{
    private const byte VehicleSystemId = 1;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLink _link = new();
    private readonly RecordingSink _sink = new();
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
            target, new FixedFactory(_link.GcsSide), _sink, _options, _time, NullLogger.Instance, new Random(1));
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

        await AdvanceAsync(TimeSpan.FromSeconds(11));

        var status = _connection.GetStatus();
        status.State.ShouldBe(ConnectionState.Faulted);
        status.FaultReason.ShouldBe("No heartbeat within 10 s.");
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

        await AdvanceAsync(TimeSpan.FromSeconds(3.5));

        _connection.State.ShouldBe(ConnectionState.Reconnecting);
    }

    [Fact]
    public async Task Heartbeat_during_reconnect_restores_the_link_and_resets_attempts()
    {
        await ConnectAsync();
        await AdvanceAsync(TimeSpan.FromSeconds(3.5)); // lost
        await AdvanceAsync(TimeSpan.FromSeconds(5));   // first attempt waited 1 s and then timed out after 3 s
        _connection.GetStatus().ReconnectAttempts.ShouldBe(1);

        // The vehicle is back. The link is in its 2 s backoff wait, so the heartbeat is read when the next attempt opens.
        await SendFromVehicleAsync(VehicleHeartbeat());
        await AdvanceAsync(TimeSpan.FromSeconds(2.5));

        await EventuallyAsync(() => _connection.State == ConnectionState.Connected);
        _connection.GetStatus().ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task Reconnecting_gives_up_after_the_maximum_attempts()
    {
        await ConnectAsync();

        // Lost after 3 s, then 3 attempts: wait 1 s + 3 s, wait 2 s + 3 s, wait 4 s + 3 s = 16 s. 25 s is plenty.
        await AdvanceAsync(TimeSpan.FromSeconds(25));

        var status = _connection.GetStatus();
        status.State.ShouldBe(ConnectionState.Faulted);
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

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

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
            _time.Advance(Step);
            await Task.Delay(2, Ct); // real time: give continuations a chance to run
        }
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

    private sealed class RecordingSink : ITelemetrySink
    {
        private readonly ConcurrentQueue<TelemetryUpdate> _updates = new();

        public IEnumerable<TelemetryUpdate> Updates => _updates;

        public void Publish(VehicleId vehicleId, TelemetryUpdate update) => _updates.Enqueue(update);
    }
}
