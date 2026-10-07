using System.ComponentModel.DataAnnotations;

namespace Gcs.Telemetry;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// How often live telemetry is pushed to clients. Vehicles send 10–50 Hz; a human cannot read faster than a few
    /// updates per second, and every push costs bandwidth for every subscriber. 200 ms = 5 Hz.
    /// </summary>
    [Range(50, 5000)]
    public int BroadcastIntervalMilliseconds { get; init; } = 200;

    /// <summary>One history sample per vehicle per interval. 1 s gives a good flight track at a tiny fraction of the message rate.</summary>
    [Range(100, 60_000)]
    public int HistorySampleIntervalMilliseconds { get; init; } = 1000;

    /// <summary>
    /// Samples buffered in memory while the database is slow or down. When full, the oldest samples are dropped:
    /// losing old history is acceptable, blocking the live telemetry path is not.
    /// </summary>
    [Range(100, 1_000_000)]
    public int HistoryBufferCapacity { get; init; } = 10_000;

    /// <summary>Write accumulated samples at least this often.</summary>
    [Range(100, 60_000)]
    public int HistoryFlushIntervalMilliseconds { get; init; } = 2000;

    /// <summary>Or as soon as this many samples are waiting.</summary>
    [Range(1, 10_000)]
    public int HistoryBatchSize { get; init; } = 500;

    /// <summary>Samples older than this are deleted. 0 keeps history forever.</summary>
    [Range(0, 3650)]
    public int HistoryRetentionDays { get; init; } = 30;

    [Range(1, 1440)]
    public int RetentionSweepIntervalMinutes { get; init; } = 60;
}
