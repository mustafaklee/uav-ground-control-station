namespace Gcs.Mavlink.Protocol;

/// <summary>
/// One validated MAVLink frame: header fields plus the raw payload, before the payload is decoded into a message.
/// The payload is kept as received: MAVLink 2 senders strip trailing zero bytes, so it can be shorter than the
/// message definition, and decoders treat the missing bytes as zero.
/// </summary>
public sealed record MavlinkFrame(
    MavlinkVersion Version,
    byte Sequence,
    byte SystemId,
    byte ComponentId,
    uint MessageId,
    byte[] Payload,
    bool IsSigned);

public enum MavlinkVersion
{
    V1 = 1,
    V2 = 2,
}

/// <summary>Constants of the MAVLink wire format.</summary>
public static class MavlinkWire
{
    public const byte StartV1 = 0xFE;
    public const byte StartV2 = 0xFD;

    public const int HeaderLengthV1 = 6;
    public const int HeaderLengthV2 = 10;
    public const int ChecksumLength = 2;
    public const int SignatureLength = 13;
    public const int MaxPayloadLength = 255;

    /// <summary>Incompatibility flag: the frame carries a 13-byte signature after the checksum.</summary>
    public const byte IncompatFlagSigned = 0x01;

    public const int MaxFrameLength = HeaderLengthV2 + MaxPayloadLength + ChecksumLength + SignatureLength;
}
