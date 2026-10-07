namespace Gcs.Mavlink.Protocol;

/// <summary>
/// CRC-16/MCRF4XX ("X.25") used by MAVLink. The checksum covers the header (without the start byte) and the payload,
/// and is then seeded with the message's CRC_EXTRA byte. CRC_EXTRA is derived from the message definition, so a
/// sender and receiver that disagree about a message's fields reject each other's frames instead of misreading them.
/// </summary>
public static class MavlinkCrc
{
    public const ushort Seed = 0xFFFF;

    public static ushort Accumulate(byte value, ushort crc)
    {
        var tmp = (byte)(value ^ (byte)(crc & 0xFF));
        tmp ^= (byte)(tmp << 4);
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    public static ushort Compute(ReadOnlySpan<byte> data, byte crcExtra)
    {
        var crc = Seed;
        foreach (var value in data)
        {
            crc = Accumulate(value, crc);
        }

        return Accumulate(crcExtra, crc);
    }
}
