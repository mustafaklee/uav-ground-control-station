namespace Gcs.Domain.Vehicles.Connections;

/// <summary>How usable a vehicle link is, at a glance. Ordered from worst to best.</summary>
public enum LinkQualityGrade
{
    /// <summary>Not connected, or nothing heard for longer than <see cref="LinkQualityRules.SilenceLimit"/>.</summary>
    Lost = 1,

    /// <summary>Heavy loss or a long round trip: commands may time out, the picture lags.</summary>
    Poor = 2,

    /// <summary>Noticeable loss or delay: usable, worth watching.</summary>
    Fair = 3,

    Good = 4,
}

/// <summary>
/// Turns link measurements into a <see cref="LinkQualityGrade"/>. The thresholds are a starting point taken from common
/// ground-station practice (ADR-019) and live here only, so tuning them after field use is a one-line change.
/// </summary>
public static class LinkQualityRules
{
    /// <summary>No frame for this long means the link is lost for the operator, even before the heartbeat watchdog acts.</summary>
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(3);

    public const double PoorLossRatio = 0.15;
    public const double FairLossRatio = 0.03;
    public const double PoorRoundTripMilliseconds = 1000;
    public const double FairRoundTripMilliseconds = 300;

    /// <param name="state">The link's connection state.</param>
    /// <param name="sinceLastFrame">Time since the last frame from the vehicle; null when none arrived yet.</param>
    /// <param name="recentLossRatio">Lost / (arrived + lost) over the recent window, 0 to 1.</param>
    /// <param name="roundTripMilliseconds">Smoothed round-trip time; null when not measured (yet).</param>
    public static LinkQualityGrade Grade(ConnectionState state, TimeSpan? sinceLastFrame, double recentLossRatio, double? roundTripMilliseconds)
    {
        if (state != ConnectionState.Connected || sinceLastFrame is null || sinceLastFrame > SilenceLimit)
        {
            return LinkQualityGrade.Lost;
        }

        if (recentLossRatio >= PoorLossRatio || roundTripMilliseconds >= PoorRoundTripMilliseconds)
        {
            return LinkQualityGrade.Poor;
        }

        return recentLossRatio >= FairLossRatio || roundTripMilliseconds >= FairRoundTripMilliseconds
            ? LinkQualityGrade.Fair
            : LinkQualityGrade.Good;
    }
}
