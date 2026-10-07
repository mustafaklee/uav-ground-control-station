using Gcs.Application.Abstractions;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Telemetry;
using Gcs.Telemetry.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gcs.UnitTests.Telemetry;

public sealed class TelemetryBroadcasterTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly LatestTelemetryStore _store = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly TelemetryBroadcaster _broadcaster;

    public TelemetryBroadcasterTests()
    {
        _broadcaster = new TelemetryBroadcaster(
            _store, _publisher, Options.Create(new TelemetryOptions()), TimeProvider.System, NullLogger<TelemetryBroadcaster>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _broadcaster.Dispose();

    [Fact]
    public async Task Each_tick_pushes_only_the_latest_snapshot_however_many_messages_arrived()
    {
        var vehicle = VehicleId.New();
        for (var i = 0; i < 10; i++)
        {
            _store.Apply(vehicle, new TelemetryUpdate(T0.AddMilliseconds(i * 20), Position: new GeoPosition(39.9 + (i * 0.001), 32.8, 1000, 100)));
        }

        await _broadcaster.BroadcastChangesAsync(Ct);

        var pushed = _publisher.Telemetry.ShouldHaveSingleItem();
        pushed.Position!.Latitude.ShouldBe(39.909, tolerance: 1e-9);
    }

    [Fact]
    public async Task Unchanged_vehicles_are_not_pushed_again()
    {
        var moving = VehicleId.New();
        var idle = VehicleId.New();
        _store.Apply(moving, new TelemetryUpdate(T0, Gps: new GpsState(GpsFix.Fix3D, 10)));
        _store.Apply(idle, new TelemetryUpdate(T0, Gps: new GpsState(GpsFix.Fix3D, 10)));
        await _broadcaster.BroadcastChangesAsync(Ct);

        _store.Apply(moving, new TelemetryUpdate(T0.AddSeconds(1), Gps: new GpsState(GpsFix.Fix3D, 11)));
        var pushedSecondTick = await _broadcaster.BroadcastChangesAsync(Ct);

        pushedSecondTick.ShouldBe(1);
        _publisher.Telemetry.Count(t => t.VehicleId == idle.Value).ShouldBe(1);
        _publisher.Telemetry.Count(t => t.VehicleId == moving.Value).ShouldBe(2);
    }

    [Fact]
    public async Task A_failing_push_does_not_stop_other_vehicles()
    {
        _publisher.FailFor = VehicleId.New();
        _store.Apply(_publisher.FailFor.Value, new TelemetryUpdate(T0, Gps: new GpsState(GpsFix.Fix3D, 10)));
        var healthy = VehicleId.New();
        _store.Apply(healthy, new TelemetryUpdate(T0, Gps: new GpsState(GpsFix.Fix3D, 10)));

        await _broadcaster.BroadcastChangesAsync(Ct);

        _publisher.Telemetry.ShouldContain(t => t.VehicleId == healthy.Value);
    }

    private sealed class RecordingPublisher : ILiveUpdatePublisher
    {
        public List<TelemetryResponse> Telemetry { get; } = [];

        public VehicleId? FailFor { get; set; }

        public Task PublishTelemetryAsync(TelemetryResponse telemetry, CancellationToken cancellationToken)
        {
            if (FailFor is { } failing && telemetry.VehicleId == failing.Value)
            {
                throw new InvalidOperationException("client disconnected");
            }

            Telemetry.Add(telemetry);
            return Task.CompletedTask;
        }

        public Task PublishLinkStatusAsync(VehicleLinkResponse status, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
