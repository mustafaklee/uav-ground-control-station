using Gcs.Application.Abstractions;
using Gcs.Domain.Vehicles.Connections;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Connections;

/// <summary>
/// Recent link quality for one vehicle (ADR-019): a sliding window of one-second buckets for frames and losses,
/// a smoothed TIMESYNC round trip, and the radio's last RADIO_STATUS. Not thread-safe: the owner calls it under its lock.
/// <code>
///  seconds:  ... | t-3 | t-2 | t-1 |  t  |      ← ring of WindowSeconds buckets
///  received:      48    50    12    9         recent loss = Σlost / (Σreceived + Σlost)
///  lost:           0     0    38    3         rate        = Σreceived / window length
/// </code>
/// </summary>
internal sealed class LinkQualityMonitor(TimeProvider time)
{
    public const int WindowSeconds = 10;

    /// <summary>Weight of the newest round-trip sample. 0.3: settles in a few seconds, ignores a single slow answer.</summary>
    public const double RoundTripSmoothing = 0.3;

    /// <summary>Echoes older than this are not ours (another GCS's request, or a reply from a previous session).</summary>
    public static readonly TimeSpan MaxRoundTrip = TimeSpan.FromSeconds(10);

    private readonly long[] _received = new long[WindowSeconds];
    private readonly long[] _lost = new long[WindowSeconds];
    private readonly long[] _bucketSecond = new long[WindowSeconds];
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private readonly long _clockOrigin = time.GetTimestamp();
    private long _lastReceivedTotal;
    private long _lastLostTotal;
    private double? _roundTripMilliseconds;
    private DateTimeOffset? _lastFrameAt;
    private RadioLinkStatus? _radio;

    /// <summary>Takes the parser's running totals after each receive; the difference since the last call goes into the current second.</summary>
    public void RecordTotals(long receivedTotal, long lostTotal, bool frameArrived)
    {
        var now = time.GetUtcNow();
        var index = Bucket(now);
        _received[index] += Math.Max(0, receivedTotal - _lastReceivedTotal);
        _lost[index] += Math.Max(0, lostTotal - _lastLostTotal);
        _lastReceivedTotal = receivedTotal;
        _lastLostTotal = lostTotal;
        if (frameArrived)
        {
            _lastFrameAt = now;
        }
    }

    /// <summary>
    /// Nanoseconds since this monitor started, the <c>ts1</c> of an outgoing TIMESYNC request. Only differences of our
    /// own clock matter, so counting from our own start avoids overflowing a raw high-resolution timestamp.
    /// </summary>
    public long NowNanoseconds() => time.GetElapsedTime(_clockOrigin).Ticks * 100;

    /// <summary>A TIMESYNC answer from the vehicle. Requests from the vehicle and echoes that are not plausibly ours are ignored.</summary>
    public void RecordTimesync(TimesyncMessage message)
    {
        if (message.IsRequest)
        {
            return;
        }

        var roundTrip = TimeSpan.FromTicks((NowNanoseconds() - message.Ts1) / 100);
        if (roundTrip < TimeSpan.Zero || roundTrip > MaxRoundTrip)
        {
            return;
        }

        var sample = roundTrip.TotalMilliseconds;
        _roundTripMilliseconds = _roundTripMilliseconds is { } previous
            ? previous + (RoundTripSmoothing * (sample - previous))
            : sample;
    }

    public void RecordRadio(RadioStatusMessage message) => _radio = new RadioLinkStatus(
        message.Rssi, message.RemoteRssi, message.Noise, message.RemoteNoise,
        message.ReceiveErrors, message.Corrected, message.TxBufferPercent, time.GetUtcNow());

    public LinkQuality Snapshot(MavlinkLinkStatistics totals, ConnectionState state)
    {
        var now = time.GetUtcNow();
        var current = Second(now);
        long received = 0, lost = 0;
        for (var i = 0; i < WindowSeconds; i++)
        {
            if (current - _bucketSecond[i] < WindowSeconds)
            {
                received += _received[i];
                lost += _lost[i];
            }
        }

        // Early in a link the window is not full yet: divide by the time actually covered, at least one second.
        var covered = Math.Clamp((now - _startedAt).TotalSeconds, 1, WindowSeconds);
        var recentLoss = received + lost == 0 ? 0 : lost / (double)(received + lost);
        var sinceLastFrame = now - _lastFrameAt;
        return new LinkQuality(
            totals.FramesReceived,
            totals.FramesLost,
            totals.PacketLossRatio,
            totals.CrcErrors,
            recentLoss,
            received / covered,
            _roundTripMilliseconds,
            _lastFrameAt,
            LinkQualityRules.Grade(state, sinceLastFrame, recentLoss, _roundTripMilliseconds),
            _radio);
    }

    private static long Second(DateTimeOffset at) => at.ToUnixTimeSeconds();

    /// <summary>The bucket for <paramref name="now"/>, cleared first if it still holds a second that left the window.</summary>
    private int Bucket(DateTimeOffset now)
    {
        var second = Second(now);
        var index = (int)(second % WindowSeconds);
        if (_bucketSecond[index] != second)
        {
            _bucketSecond[index] = second;
            _received[index] = 0;
            _lost[index] = 0;
        }

        return index;
    }
}
