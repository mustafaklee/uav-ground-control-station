namespace Gcs.Mavlink.Protocol;

/// <summary>
/// Link quality counters. Every MAVLink frame carries an 8-bit sequence number per sender, so gaps reveal how many
/// frames were lost on the way, which is the basis for packet loss and link quality indicators.
/// </summary>
public sealed class MavlinkLinkStatistics
{
    private readonly Dictionary<ushort, byte> _lastSequence = [];

    public long FramesReceived { get; internal set; }

    /// <summary>Frames that never arrived, inferred from gaps in the sender's sequence numbers.</summary>
    public long FramesLost { get; internal set; }

    public long CrcErrors { get; internal set; }

    public long UnknownMessages { get; internal set; }

    public long FramesRejected { get; internal set; }

    public long BytesDiscarded { get; internal set; }

    /// <summary>Lost / (received + lost), between 0 and 1.</summary>
    public double PacketLossRatio =>
        FramesReceived + FramesLost == 0 ? 0 : FramesLost / (double)(FramesReceived + FramesLost);

    internal void TrackSequence(byte systemId, byte componentId, byte sequence)
    {
        var sender = (ushort)((systemId << 8) | componentId);
        if (_lastSequence.TryGetValue(sender, out var last))
        {
            // byte arithmetic wraps 255 → 0, which is exactly how MAVLink sequence numbers wrap.
            var gap = (byte)(sequence - last - 1);
            FramesLost += gap;
        }

        _lastSequence[sender] = sequence;
    }
}
