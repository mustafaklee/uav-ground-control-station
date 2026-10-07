using System.Buffers.Binary;

namespace Gcs.Mavlink.Protocol.Messages;

// The MAVLink mission protocol (https://mavlink.io/en/services/mission.html), "INT" variant: coordinates as 1e-7 degrees
// in int32, which is exact to ~1 cm, unlike the float variant. Base fields only; mission_type extension = 0 (mission).

/// <summary>MISSION_REQUEST_LIST (#43): GCS asks the vehicle for its mission (download starts).</summary>
public sealed record MissionRequestListMessage(byte TargetSystem, byte TargetComponent) : IMavlinkMessage
{
    public const uint Id = 43;
    public const int Length = 2;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new MissionRequestListMessage(p[0], p[1]);

    public void Write(Span<byte> payload)
    {
        payload[0] = TargetSystem;
        payload[1] = TargetComponent;
    }
}

/// <summary>MISSION_COUNT (#44): "the mission has N items". Starts an upload (GCS → vehicle) or answers a request list.</summary>
public sealed record MissionCountMessage(byte TargetSystem, byte TargetComponent, ushort Count) : IMavlinkMessage
{
    public const uint Id = 44;
    public const int Length = 4;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) =>
        new MissionCountMessage(p[2], p[3], BinaryPrimitives.ReadUInt16LittleEndian(p));

    public void Write(Span<byte> payload)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(payload, Count);
        payload[2] = TargetSystem;
        payload[3] = TargetComponent;
    }
}

/// <summary>MISSION_REQUEST_INT (#51): "send me item number Seq". The receiver of a mission drives the transfer.</summary>
public sealed record MissionRequestIntMessage(byte TargetSystem, byte TargetComponent, ushort Seq) : IMavlinkMessage
{
    public const uint Id = 51;
    public const int Length = 4;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) =>
        new MissionRequestIntMessage(p[2], p[3], BinaryPrimitives.ReadUInt16LittleEndian(p));

    public void Write(Span<byte> payload)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(payload, Seq);
        payload[2] = TargetSystem;
        payload[3] = TargetComponent;
    }
}

/// <summary>
/// MISSION_ITEM_INT (#73): one mission item. X/Y are latitude/longitude in 1e-7 degrees (for global frames),
/// Z the altitude in metres; the meaning of Param1–4 depends on <see cref="Command"/>.
/// </summary>
public sealed record MissionItemIntMessage(
    byte TargetSystem,
    byte TargetComponent,
    ushort Seq,
    MavFrame Frame,
    MavCmd Command,
    byte Current,
    byte Autocontinue,
    float Param1,
    float Param2,
    float Param3,
    float Param4,
    int X,
    int Y,
    float Z) : IMavlinkMessage
{
    public const uint Id = 73;
    public const int Length = 37;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new MissionItemIntMessage(
        p[32],
        p[33],
        BinaryPrimitives.ReadUInt16LittleEndian(p[28..]),
        (MavFrame)p[34],
        (MavCmd)BinaryPrimitives.ReadUInt16LittleEndian(p[30..]),
        p[35],
        p[36],
        BinaryPrimitives.ReadSingleLittleEndian(p),
        BinaryPrimitives.ReadSingleLittleEndian(p[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[8..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[12..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[16..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[20..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[24..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteSingleLittleEndian(p, Param1);
        BinaryPrimitives.WriteSingleLittleEndian(p[4..], Param2);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], Param3);
        BinaryPrimitives.WriteSingleLittleEndian(p[12..], Param4);
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], X);
        BinaryPrimitives.WriteInt32LittleEndian(p[20..], Y);
        BinaryPrimitives.WriteSingleLittleEndian(p[24..], Z);
        BinaryPrimitives.WriteUInt16LittleEndian(p[28..], Seq);
        BinaryPrimitives.WriteUInt16LittleEndian(p[30..], (ushort)Command);
        p[32] = TargetSystem;
        p[33] = TargetComponent;
        p[34] = (byte)Frame;
        p[35] = Current;
        p[36] = Autocontinue;
    }
}

/// <summary>MISSION_ACK (#47): ends a transfer. <see cref="MavMissionResult.Accepted"/> or the reason it failed.</summary>
public sealed record MissionAckMessage(byte TargetSystem, byte TargetComponent, MavMissionResult Type) : IMavlinkMessage
{
    public const uint Id = 47;
    public const int Length = 3;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new MissionAckMessage(p[0], p[1], (MavMissionResult)p[2]);

    public void Write(Span<byte> payload)
    {
        payload[0] = TargetSystem;
        payload[1] = TargetComponent;
        payload[2] = (byte)Type;
    }
}

/// <summary>MISSION_CURRENT (#42): the item the vehicle is currently flying to.</summary>
public sealed record MissionCurrentMessage(ushort Seq) : IMavlinkMessage
{
    public const uint Id = 42;
    public const int Length = 2;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new MissionCurrentMessage(BinaryPrimitives.ReadUInt16LittleEndian(p));

    public void Write(Span<byte> payload) => BinaryPrimitives.WriteUInt16LittleEndian(payload, Seq);
}

/// <summary>MISSION_ITEM_REACHED (#46): the vehicle reached item <see cref="Seq"/>.</summary>
public sealed record MissionItemReachedMessage(ushort Seq) : IMavlinkMessage
{
    public const uint Id = 46;
    public const int Length = 2;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new MissionItemReachedMessage(BinaryPrimitives.ReadUInt16LittleEndian(p));

    public void Write(Span<byte> payload) => BinaryPrimitives.WriteUInt16LittleEndian(payload, Seq);
}
