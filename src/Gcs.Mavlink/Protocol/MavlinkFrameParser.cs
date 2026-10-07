using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Protocol;

/// <summary>
/// Turns a byte stream into MAVLink frames. Bytes may arrive in arbitrary chunks (a serial port delivers a frame in
/// pieces, a UDP datagram may hold several frames) and may contain noise, so the parser keeps unfinished data between
/// calls and resynchronizes on the next start byte when something does not add up.
/// </summary>
/// <remarks>
/// Not thread safe: one parser per link, fed by that link's receive loop.
/// Unknown message ids cannot be CRC checked (their CRC_EXTRA is unknown) and are skipped as a whole frame.
/// </remarks>
public sealed class MavlinkFrameParser
{
    private readonly byte[] _buffer = new byte[MavlinkWire.MaxFrameLength * 4];
    private int _count;

    public MavlinkLinkStatistics Statistics { get; } = new();

    /// <summary>Appends <paramref name="data"/> and returns every complete, valid frame found so far.</summary>
    public IReadOnlyList<MavlinkFrame> Parse(ReadOnlySpan<byte> data)
    {
        var frames = new List<MavlinkFrame>();
        while (!data.IsEmpty)
        {
            var take = Math.Min(data.Length, _buffer.Length - _count);
            data[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            data = data[take..];
            ExtractFrames(frames);
        }

        return frames;
    }

    private void ExtractFrames(List<MavlinkFrame> frames)
    {
        var position = 0;
        while (position < _count)
        {
            var start = _buffer.AsSpan(position, _count - position).IndexOfAny(MavlinkWire.StartV2, MavlinkWire.StartV1);
            if (start < 0)
            {
                Statistics.BytesDiscarded += _count - position;
                position = _count;
                break;
            }

            Statistics.BytesDiscarded += start;
            position += start;

            var result = TryReadFrame(_buffer.AsSpan(position, _count - position), out var frame, out var consumed);
            if (result == ReadResult.NeedMoreData)
            {
                break;
            }

            if (result == ReadResult.Frame)
            {
                frames.Add(frame!);
                Statistics.FramesReceived++;
                Statistics.TrackSequence(frame!.SystemId, frame.ComponentId, frame.Sequence);
            }

            position += consumed;
        }

        // Keep the unfinished tail for the next call.
        _buffer.AsSpan(position, _count - position).CopyTo(_buffer);
        _count -= position;
    }

    private ReadResult TryReadFrame(ReadOnlySpan<byte> data, out MavlinkFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 1; // on any error, step over this start byte and look for the next one

        var isV2 = data[0] == MavlinkWire.StartV2;
        var headerLength = isV2 ? MavlinkWire.HeaderLengthV2 : MavlinkWire.HeaderLengthV1;
        if (data.Length < headerLength)
        {
            return ReadResult.NeedMoreData;
        }

        int payloadLength = data[1];
        var signed = isV2 && (data[2] & MavlinkWire.IncompatFlagSigned) != 0;
        if (isV2 && (data[2] & ~MavlinkWire.IncompatFlagSigned) != 0)
        {
            // Unknown incompatibility flag: by definition we must not try to interpret this frame.
            Statistics.FramesRejected++;
            return ReadResult.Invalid;
        }

        var frameLength = headerLength + payloadLength + MavlinkWire.ChecksumLength + (signed ? MavlinkWire.SignatureLength : 0);
        if (data.Length < frameLength)
        {
            return ReadResult.NeedMoreData;
        }

        var sequence = isV2 ? data[4] : data[2];
        var systemId = isV2 ? data[5] : data[3];
        var componentId = isV2 ? data[6] : data[4];
        var messageId = isV2 ? (uint)(data[7] | (data[8] << 8) | (data[9] << 16)) : data[5];

        if (!MavlinkMessageRegistry.TryGet(messageId, out var info))
        {
            Statistics.UnknownMessages++;
            consumed = frameLength;
            return ReadResult.Skipped;
        }

        var crcOffset = headerLength + payloadLength;
        var expected = (ushort)(data[crcOffset] | (data[crcOffset + 1] << 8));
        var actual = MavlinkCrc.Compute(data[1..crcOffset], info.CrcExtra);
        if (actual != expected)
        {
            Statistics.CrcErrors++;
            return ReadResult.Invalid;
        }

        frame = new MavlinkFrame(
            isV2 ? MavlinkVersion.V2 : MavlinkVersion.V1,
            sequence,
            systemId,
            componentId,
            messageId,
            data.Slice(headerLength, payloadLength).ToArray(),
            signed);
        consumed = frameLength;
        return ReadResult.Frame;
    }

    private enum ReadResult
    {
        Frame,
        Skipped,
        Invalid,
        NeedMoreData,
    }
}
