using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Protocol;

/// <summary>Encodes messages into MAVLink 2 frames and decodes frames back into messages.</summary>
public static class MavlinkCodec
{
    /// <summary>
    /// Builds a MAVLink 2 frame. Trailing zero bytes of the payload are removed (MAVLink 2 payload truncation),
    /// but at least one payload byte is always kept.
    /// </summary>
    public static byte[] Encode(IMavlinkMessage message, byte sequence, byte systemId, byte componentId)
    {
        ArgumentNullException.ThrowIfNull(message);
        var info = MavlinkMessageRegistry.Get(message.MessageId);

        Span<byte> payload = stackalloc byte[info.Length];
        payload.Clear();
        message.Write(payload);

        var length = payload.Length;
        while (length > 1 && payload[length - 1] == 0)
        {
            length--;
        }

        var frame = new byte[MavlinkWire.HeaderLengthV2 + length + MavlinkWire.ChecksumLength];
        frame[0] = MavlinkWire.StartV2;
        frame[1] = (byte)length;
        frame[2] = 0; // incompatibility flags: not signed
        frame[3] = 0; // compatibility flags
        frame[4] = sequence;
        frame[5] = systemId;
        frame[6] = componentId;
        frame[7] = (byte)(message.MessageId & 0xFF);
        frame[8] = (byte)((message.MessageId >> 8) & 0xFF);
        frame[9] = (byte)((message.MessageId >> 16) & 0xFF);
        payload[..length].CopyTo(frame.AsSpan(MavlinkWire.HeaderLengthV2));

        var crcOffset = MavlinkWire.HeaderLengthV2 + length;
        var crc = MavlinkCrc.Compute(frame.AsSpan(1, crcOffset - 1), info.CrcExtra);
        frame[crcOffset] = (byte)(crc & 0xFF);
        frame[crcOffset + 1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>Decodes a validated frame. Returns false for message ids this GCS does not know.</summary>
    public static bool TryDecode(MavlinkFrame frame, out IMavlinkMessage? message)
    {
        ArgumentNullException.ThrowIfNull(frame);
        message = null;
        if (!MavlinkMessageRegistry.TryGet(frame.MessageId, out var info))
        {
            return false;
        }

        // Zero-pad: a truncated MAVLink 2 payload means the missing trailing fields are zero.
        Span<byte> padded = stackalloc byte[Math.Max(info.Length, frame.Payload.Length)];
        padded.Clear();
        frame.Payload.CopyTo(padded);
        message = info.Read(padded);
        return true;
    }
}
