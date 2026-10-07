namespace Gcs.Mavlink.Connections;

/// <summary>
/// Exponential backoff with jitter: base, 2×base, 4×base... capped at a maximum, each spread by a random ±ratio.
/// Retrying immediately and forever would flood a struggling link (and every other vehicle on it).
/// </summary>
public static class ReconnectBackoff
{
    public static TimeSpan Delay(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double jitterRatio, Random random)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentNullException.ThrowIfNull(random);

        var exponential = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 30));
        var capped = Math.Min(exponential, maxDelay.TotalMilliseconds);
        var jitter = 1 + (((random.NextDouble() * 2) - 1) * jitterRatio);
        return TimeSpan.FromMilliseconds(capped * jitter);
    }
}
