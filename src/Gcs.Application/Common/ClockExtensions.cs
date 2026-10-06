namespace Gcs.Application.Common;

internal static class ClockExtensions
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>
    /// Current UTC time truncated to whole microseconds, the precision PostgreSQL stores.
    /// Without this, a response built from the in-memory entity (100 ns precision) would differ from the same entity
    /// read back from the database, and clients comparing timestamps would see phantom changes.
    /// </summary>
    public static DateTimeOffset GetUtcNowForStorage(this TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TicksPerMicrosecond));
    }
}
