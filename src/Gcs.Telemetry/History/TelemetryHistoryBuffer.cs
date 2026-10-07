using System.Collections.Concurrent;
using System.Threading.Channels;
using Gcs.Application.Abstractions;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Microsoft.Extensions.Options;

namespace Gcs.Telemetry.History;

/// <summary>
/// Decides which snapshots become history samples (at most one per vehicle per interval) and queues them for the
/// batch writer. Offering a sample never blocks: the queue is bounded and drops its oldest entry when full.
/// </summary>
internal sealed class TelemetryHistoryBuffer
{
    private readonly ConcurrentDictionary<VehicleId, DateTimeOffset> _lastSampled = new();
    private readonly TimeSpan _interval;
    private readonly Channel<TelemetrySample> _queue;
    private long _dropped;

    public TelemetryHistoryBuffer(IOptions<TelemetryOptions> options)
    {
        var settings = options.Value;
        _interval = TimeSpan.FromMilliseconds(settings.HistorySampleIntervalMilliseconds);
        _queue = Channel.CreateBounded<TelemetrySample>(
            new BoundedChannelOptions(settings.HistoryBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public ChannelReader<TelemetrySample> Reader => _queue.Reader;

    /// <summary>Samples lost because the buffer was full (database too slow or unavailable).</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    public void Offer(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = snapshot.UpdatedAt;
        var due = true;
        _lastSampled.AddOrUpdate(
            snapshot.VehicleId,
            now,
            (_, last) =>
            {
                due = now - last >= _interval;
                return due ? now : last;
            });

        if (due)
        {
            _queue.Writer.TryWrite(TelemetrySample.From(snapshot, now));
        }
    }
}
