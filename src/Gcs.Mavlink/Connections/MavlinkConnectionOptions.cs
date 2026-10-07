using System.ComponentModel.DataAnnotations;

namespace Gcs.Mavlink.Connections;

public sealed class MavlinkConnectionOptions
{
    public const string SectionName = "Mavlink";

    /// <summary>System id the GCS uses in its own messages. 255 is the conventional GCS id.</summary>
    [Range(1, 255)]
    public int GcsSystemId { get; init; } = 255;

    /// <summary>How often the GCS sends its own HEARTBEAT (the MAVLink standard rate is 1 Hz).</summary>
    [Range(100, 10_000)]
    public int HeartbeatIntervalMilliseconds { get; init; } = 1000;

    /// <summary>
    /// Without a vehicle heartbeat for this long, the link is considered lost.
    /// Three missed 1 Hz heartbeats is the usual choice: one lost datagram must not trigger a reconnect.
    /// </summary>
    [Range(500, 60_000)]
    public int HeartbeatTimeoutMilliseconds { get; init; } = 3000;

    /// <summary>How long a first connection attempt waits for the vehicle's first heartbeat before faulting.</summary>
    [Range(1000, 120_000)]
    public int ConnectTimeoutMilliseconds { get; init; } = 10_000;

    /// <summary>Reconnect attempts after a lost link before giving up (Faulted). Bounded on purpose: no endless loop.</summary>
    [Range(1, 100)]
    public int MaxReconnectAttempts { get; init; } = 5;

    /// <summary>Delay before the first reconnect attempt; doubles every attempt (exponential backoff).</summary>
    [Range(100, 60_000)]
    public int ReconnectBaseDelayMilliseconds { get; init; } = 1000;

    [Range(100, 600_000)]
    public int ReconnectMaxDelayMilliseconds { get; init; } = 30_000;

    /// <summary>
    /// Random spread added to each backoff delay (0.2 = ±20%). When many vehicles drop at once (e.g. a radio relay
    /// fails), jitter keeps them from all retrying in the same instant.
    /// </summary>
    [Range(0.0, 1.0)]
    public double ReconnectJitterRatio { get; init; } = 0.2;

    /// <summary>How often the watchdog checks heartbeat age.</summary>
    [Range(50, 5000)]
    public int WatchdogIntervalMilliseconds { get; init; } = 250;
}
