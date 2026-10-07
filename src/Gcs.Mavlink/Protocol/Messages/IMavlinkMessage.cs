namespace Gcs.Mavlink.Protocol.Messages;

/// <summary>A decoded MAVLink message that can also be encoded again.</summary>
public interface IMavlinkMessage
{
    uint MessageId { get; }

    /// <summary>Writes the payload in wire order into a buffer of exactly the message's full length.</summary>
    void Write(Span<byte> payload);
}

/// <summary>
/// Static description of a message type. <paramref name="Length"/> is the length of the base (non-extension) fields;
/// extension fields added in MAVLink 2 are ignored when reading and not written.
/// </summary>
public sealed record MavlinkMessageInfo(
    uint Id,
    string Name,
    byte CrcExtra,
    int Length,
    MavlinkMessageReader Read);

/// <summary>Decodes a payload that has been zero-padded to the message's full length.</summary>
public delegate IMavlinkMessage MavlinkMessageReader(ReadOnlySpan<byte> payload);
