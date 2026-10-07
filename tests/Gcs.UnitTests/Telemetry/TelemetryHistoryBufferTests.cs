using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Telemetry;
using Gcs.Telemetry.History;
using Microsoft.Extensions.Options;

namespace Gcs.UnitTests.Telemetry;

public sealed class TelemetryHistoryBufferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void At_most_one_sample_per_vehicle_per_interval_is_queued()
    {
        var buffer = NewBuffer(sampleIntervalMs: 1000);
        var vehicle = VehicleId.New();

        // 50 Hz for 3 seconds = 150 snapshots → 3 samples (t = 0 s, 1 s, 2 s).
        for (var i = 0; i < 150; i++)
        {
            buffer.Offer(Snapshot(vehicle, T0.AddMilliseconds(i * 20)));
        }

        Drain(buffer).Select(s => s.RecordedAt).ShouldBe([T0, T0.AddSeconds(1), T0.AddSeconds(2)]);
    }

    [Fact]
    public void Vehicles_are_sampled_independently()
    {
        var buffer = NewBuffer(sampleIntervalMs: 1000);
        var a = VehicleId.New();
        var b = VehicleId.New();

        buffer.Offer(Snapshot(a, T0));
        buffer.Offer(Snapshot(b, T0.AddMilliseconds(10)));

        Drain(buffer).Select(s => s.VehicleId).ShouldBe([a, b]);
    }

    [Fact]
    public void When_full_the_oldest_samples_are_dropped_and_counted()
    {
        var buffer = NewBuffer(sampleIntervalMs: 1000, capacity: 3);
        var vehicle = VehicleId.New();

        for (var second = 0; second < 5; second++)
        {
            buffer.Offer(Snapshot(vehicle, T0.AddSeconds(second)));
        }

        buffer.DroppedSamples.ShouldBe(2);
        Drain(buffer).Select(s => s.RecordedAt).ShouldBe([T0.AddSeconds(2), T0.AddSeconds(3), T0.AddSeconds(4)]);
    }

    private static TelemetryHistoryBuffer NewBuffer(int sampleIntervalMs, int capacity = 100) =>
        new(Options.Create(new TelemetryOptions { HistorySampleIntervalMilliseconds = sampleIntervalMs, HistoryBufferCapacity = capacity }));

    private static TelemetrySnapshot Snapshot(VehicleId vehicle, DateTimeOffset at) =>
        TelemetrySnapshot.Empty(vehicle, at).Apply(new TelemetryUpdate(at, Position: new GeoPosition(39.9, 32.8, 1000, 100)));

    private static List<Gcs.Application.Abstractions.TelemetrySample> Drain(TelemetryHistoryBuffer buffer)
    {
        var samples = new List<Gcs.Application.Abstractions.TelemetrySample>();
        while (buffer.Reader.TryRead(out var sample))
        {
            samples.Add(sample);
        }

        return samples;
    }
}
