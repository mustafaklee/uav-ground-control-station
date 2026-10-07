using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Telemetry;

namespace Gcs.UnitTests.Telemetry;

public sealed class LatestTelemetryStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Updates_are_merged_into_one_snapshot_per_vehicle()
    {
        var store = new LatestTelemetryStore();
        var vehicle = VehicleId.New();

        store.Apply(vehicle, new TelemetryUpdate(T0, Position: new GeoPosition(39.9, 32.8, 950, 50)));
        store.Apply(vehicle, new TelemetryUpdate(T0.AddMilliseconds(100), Battery: new BatteryState(15.8, 12.3, 76)));
        store.Apply(vehicle, new TelemetryUpdate(T0.AddMilliseconds(200), Position: new GeoPosition(39.91, 32.81, 951, 51)));

        var snapshot = store.GetLatest(vehicle)!;
        snapshot.Position!.Latitude.ShouldBe(39.91);
        snapshot.Battery!.RemainingPercent.ShouldBe(76);
        snapshot.UpdatedAt.ShouldBe(T0.AddMilliseconds(200));
    }

    [Fact]
    public void Vehicles_do_not_see_each_others_telemetry()
    {
        var store = new LatestTelemetryStore();
        var a = VehicleId.New();

        store.Apply(a, new TelemetryUpdate(T0, Gps: new GpsState(GpsFix.Fix3D, 12)));

        store.GetLatest(VehicleId.New()).ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_publishers_never_lose_parts_of_the_snapshot()
    {
        var store = new LatestTelemetryStore();
        var vehicle = VehicleId.New();

        await Task.WhenAll(
            Task.Run(() => Repeat(i => store.Apply(vehicle, new TelemetryUpdate(T0.AddTicks(i), Position: new GeoPosition(i, 0, 0, 0)))), TestContext.Current.CancellationToken),
            Task.Run(() => Repeat(i => store.Apply(vehicle, new TelemetryUpdate(T0.AddTicks(i), Gps: new GpsState(GpsFix.Fix3D, i % 20)))), TestContext.Current.CancellationToken),
            Task.Run(() => Repeat(i => store.Apply(vehicle, new TelemetryUpdate(T0.AddTicks(i), Battery: new BatteryState(16, 10, i % 100)))), TestContext.Current.CancellationToken));

        var snapshot = store.GetLatest(vehicle)!;
        snapshot.Position.ShouldNotBeNull();
        snapshot.Gps.ShouldNotBeNull();
        snapshot.Battery.ShouldNotBeNull();
    }

    private static void Repeat(Action<int> action)
    {
        for (var i = 0; i < 10_000; i++)
        {
            action(i);
        }
    }
}
