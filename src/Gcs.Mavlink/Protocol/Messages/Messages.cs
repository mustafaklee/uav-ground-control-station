using System.Buffers.Binary;

namespace Gcs.Mavlink.Protocol.Messages;

// Wire layout: MAVLink orders fields by type size (8-byte, then 4, 2, 1), all little endian.
// Offsets below follow that order; property order follows the XML definition for readability.

/// <summary>HEARTBEAT (#0): "I am alive", sent at 1 Hz by every system. Also carries type, autopilot, mode and arm state.</summary>
public sealed record HeartbeatMessage(
    MavType Type,
    MavAutopilot Autopilot,
    MavBaseMode BaseMode,
    uint CustomMode,
    MavState SystemStatus,
    byte MavlinkVersion = 3) : IMavlinkMessage
{
    public const uint Id = 0;
    public const int Length = 9;

    public uint MessageId => Id;

    public bool IsArmed => BaseMode.HasFlag(MavBaseMode.SafetyArmed);

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new HeartbeatMessage(
        (MavType)p[4], (MavAutopilot)p[5], (MavBaseMode)p[6], BinaryPrimitives.ReadUInt32LittleEndian(p), (MavState)p[7], p[8]);

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteUInt32LittleEndian(p, CustomMode);
        p[4] = (byte)Type;
        p[5] = (byte)Autopilot;
        p[6] = (byte)BaseMode;
        p[7] = (byte)SystemStatus;
        p[8] = MavlinkVersion;
    }
}

/// <summary>
/// SYS_STATUS (#1): sensor health, CPU load, main battery voltage/current/remaining, link error counters.
/// Unknown values are sent as voltage = UINT16_MAX, current = -1, remaining = -1.
/// </summary>
public sealed record SysStatusMessage(
    uint SensorsPresent,
    uint SensorsEnabled,
    uint SensorsHealth,
    ushort LoadPermille,
    ushort VoltageBatteryMillivolts,
    short CurrentBatteryCentiamps,
    sbyte BatteryRemainingPercent,
    ushort DropRateComm = 0,
    ushort ErrorsComm = 0) : IMavlinkMessage
{
    public const uint Id = 1;
    public const int Length = 31;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new SysStatusMessage(
        BinaryPrimitives.ReadUInt32LittleEndian(p),
        BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(p[8..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[12..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[14..]),
        BinaryPrimitives.ReadInt16LittleEndian(p[16..]),
        (sbyte)p[30],
        BinaryPrimitives.ReadUInt16LittleEndian(p[18..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[20..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteUInt32LittleEndian(p, SensorsPresent);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], SensorsEnabled);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], SensorsHealth);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], LoadPermille);
        BinaryPrimitives.WriteUInt16LittleEndian(p[14..], VoltageBatteryMillivolts);
        BinaryPrimitives.WriteInt16LittleEndian(p[16..], CurrentBatteryCentiamps);
        BinaryPrimitives.WriteUInt16LittleEndian(p[18..], DropRateComm);
        BinaryPrimitives.WriteUInt16LittleEndian(p[20..], ErrorsComm);
        p[22..30].Clear(); // errors_count1..4
        p[30] = (byte)BatteryRemainingPercent;
    }
}

/// <summary>GPS_RAW_INT (#24): raw GNSS fix. Lat/lon in 1e-7 degrees, altitude in millimetres (MSL).</summary>
public sealed record GpsRawIntMessage(
    ulong TimeUsec,
    GpsFixType FixType,
    int LatitudeE7,
    int LongitudeE7,
    int AltitudeMillimetres,
    ushort Eph,
    ushort Epv,
    ushort VelocityCmPerSecond,
    ushort CourseOverGroundCentidegrees,
    byte SatellitesVisible) : IMavlinkMessage
{
    public const uint Id = 24;
    public const int Length = 30;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new GpsRawIntMessage(
        BinaryPrimitives.ReadUInt64LittleEndian(p),
        (GpsFixType)p[28],
        BinaryPrimitives.ReadInt32LittleEndian(p[8..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[12..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[16..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[20..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[22..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[24..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[26..]),
        p[29]);

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteUInt64LittleEndian(p, TimeUsec);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], LatitudeE7);
        BinaryPrimitives.WriteInt32LittleEndian(p[12..], LongitudeE7);
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], AltitudeMillimetres);
        BinaryPrimitives.WriteUInt16LittleEndian(p[20..], Eph);
        BinaryPrimitives.WriteUInt16LittleEndian(p[22..], Epv);
        BinaryPrimitives.WriteUInt16LittleEndian(p[24..], VelocityCmPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(p[26..], CourseOverGroundCentidegrees);
        p[28] = (byte)FixType;
        p[29] = SatellitesVisible;
    }
}

/// <summary>ATTITUDE (#30): roll, pitch, yaw in radians and their rates in rad/s.</summary>
public sealed record AttitudeMessage(
    uint TimeBootMs,
    float Roll,
    float Pitch,
    float Yaw,
    float RollSpeed,
    float PitchSpeed,
    float YawSpeed) : IMavlinkMessage
{
    public const uint Id = 30;
    public const int Length = 28;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new AttitudeMessage(
        BinaryPrimitives.ReadUInt32LittleEndian(p),
        BinaryPrimitives.ReadSingleLittleEndian(p[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[8..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[12..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[16..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[20..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[24..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteUInt32LittleEndian(p, TimeBootMs);
        BinaryPrimitives.WriteSingleLittleEndian(p[4..], Roll);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], Pitch);
        BinaryPrimitives.WriteSingleLittleEndian(p[12..], Yaw);
        BinaryPrimitives.WriteSingleLittleEndian(p[16..], RollSpeed);
        BinaryPrimitives.WriteSingleLittleEndian(p[20..], PitchSpeed);
        BinaryPrimitives.WriteSingleLittleEndian(p[24..], YawSpeed);
    }
}

/// <summary>
/// GLOBAL_POSITION_INT (#33): fused position estimate. Lat/lon 1e-7 deg, altitudes in mm (MSL and relative to home),
/// velocities in cm/s (north, east, down), heading in centidegrees (UINT16_MAX = unknown).
/// </summary>
public sealed record GlobalPositionIntMessage(
    uint TimeBootMs,
    int LatitudeE7,
    int LongitudeE7,
    int AltitudeMslMillimetres,
    int RelativeAltitudeMillimetres,
    short VelocityNorthCmPerSecond,
    short VelocityEastCmPerSecond,
    short VelocityDownCmPerSecond,
    ushort HeadingCentidegrees) : IMavlinkMessage
{
    public const uint Id = 33;
    public const int Length = 28;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new GlobalPositionIntMessage(
        BinaryPrimitives.ReadUInt32LittleEndian(p),
        BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[8..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[12..]),
        BinaryPrimitives.ReadInt32LittleEndian(p[16..]),
        BinaryPrimitives.ReadInt16LittleEndian(p[20..]),
        BinaryPrimitives.ReadInt16LittleEndian(p[22..]),
        BinaryPrimitives.ReadInt16LittleEndian(p[24..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[26..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteUInt32LittleEndian(p, TimeBootMs);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], LatitudeE7);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], LongitudeE7);
        BinaryPrimitives.WriteInt32LittleEndian(p[12..], AltitudeMslMillimetres);
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], RelativeAltitudeMillimetres);
        BinaryPrimitives.WriteInt16LittleEndian(p[20..], VelocityNorthCmPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(p[22..], VelocityEastCmPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(p[24..], VelocityDownCmPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(p[26..], HeadingCentidegrees);
    }
}

/// <summary>VFR_HUD (#74): the values a pilot's head-up display shows: speeds (m/s), altitude (m), climb, heading (deg), throttle (%).</summary>
public sealed record VfrHudMessage(
    float Airspeed,
    float Groundspeed,
    short Heading,
    ushort ThrottlePercent,
    float Altitude,
    float Climb) : IMavlinkMessage
{
    public const uint Id = 74;
    public const int Length = 20;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new VfrHudMessage(
        BinaryPrimitives.ReadSingleLittleEndian(p),
        BinaryPrimitives.ReadSingleLittleEndian(p[4..]),
        BinaryPrimitives.ReadInt16LittleEndian(p[16..]),
        BinaryPrimitives.ReadUInt16LittleEndian(p[18..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[8..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[12..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteSingleLittleEndian(p, Airspeed);
        BinaryPrimitives.WriteSingleLittleEndian(p[4..], Groundspeed);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], Altitude);
        BinaryPrimitives.WriteSingleLittleEndian(p[12..], Climb);
        BinaryPrimitives.WriteInt16LittleEndian(p[16..], Heading);
        BinaryPrimitives.WriteUInt16LittleEndian(p[18..], ThrottlePercent);
    }
}

/// <summary>COMMAND_LONG (#76): a command with up to seven float parameters, answered by COMMAND_ACK.</summary>
public sealed record CommandLongMessage(
    byte TargetSystem,
    byte TargetComponent,
    MavCmd Command,
    byte Confirmation,
    float Param1 = 0,
    float Param2 = 0,
    float Param3 = 0,
    float Param4 = 0,
    float Param5 = 0,
    float Param6 = 0,
    float Param7 = 0) : IMavlinkMessage
{
    public const uint Id = 76;
    public const int Length = 33;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new CommandLongMessage(
        p[30],
        p[31],
        (MavCmd)BinaryPrimitives.ReadUInt16LittleEndian(p[28..]),
        p[32],
        BinaryPrimitives.ReadSingleLittleEndian(p),
        BinaryPrimitives.ReadSingleLittleEndian(p[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[8..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[12..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[16..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[20..]),
        BinaryPrimitives.ReadSingleLittleEndian(p[24..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteSingleLittleEndian(p, Param1);
        BinaryPrimitives.WriteSingleLittleEndian(p[4..], Param2);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], Param3);
        BinaryPrimitives.WriteSingleLittleEndian(p[12..], Param4);
        BinaryPrimitives.WriteSingleLittleEndian(p[16..], Param5);
        BinaryPrimitives.WriteSingleLittleEndian(p[20..], Param6);
        BinaryPrimitives.WriteSingleLittleEndian(p[24..], Param7);
        BinaryPrimitives.WriteUInt16LittleEndian(p[28..], (ushort)Command);
        p[30] = TargetSystem;
        p[31] = TargetComponent;
        p[32] = Confirmation;
    }
}

/// <summary>COMMAND_ACK (#77): the vehicle's answer to a command.</summary>
public sealed record CommandAckMessage(MavCmd Command, MavResult Result) : IMavlinkMessage
{
    public const uint Id = 77;
    public const int Length = 3;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) =>
        new CommandAckMessage((MavCmd)BinaryPrimitives.ReadUInt16LittleEndian(p), (MavResult)p[2]);

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)Command);
        p[2] = (byte)Result;
    }
}

/// <summary>
/// RADIO_STATUS (#109): sent by telemetry radios (SiK and others) about the radio link itself, not the vehicle.
/// RSSI and noise are in the radio's own units (SiK: 0-255, roughly 2 dB per step); "remote" values are the other end's.
/// </summary>
public sealed record RadioStatusMessage(
    byte Rssi,
    byte RemoteRssi,
    byte TxBufferPercent,
    byte Noise,
    byte RemoteNoise,
    ushort ReceiveErrors,
    ushort Corrected) : IMavlinkMessage
{
    public const uint Id = 109;
    public const int Length = 9;

    public uint MessageId => Id;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new RadioStatusMessage(
        p[4], p[5], p[6], p[7], p[8],
        BinaryPrimitives.ReadUInt16LittleEndian(p),
        BinaryPrimitives.ReadUInt16LittleEndian(p[2..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteUInt16LittleEndian(p, ReceiveErrors);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], Corrected);
        p[4] = Rssi;
        p[5] = RemoteRssi;
        p[6] = TxBufferPercent;
        p[7] = Noise;
        p[8] = RemoteNoise;
    }
}

/// <summary>
/// TIMESYNC (#111). A request has <see cref="Tc1"/> = 0 and <see cref="Ts1"/> = the sender's clock (nanoseconds); the
/// answer echoes <see cref="Ts1"/> and puts the responder's clock in <see cref="Tc1"/>. The GCS uses the echo for the
/// round-trip time: only its own clock is compared, so the two clocks need not agree.
/// </summary>
public sealed record TimesyncMessage(long Tc1, long Ts1) : IMavlinkMessage
{
    public const uint Id = 111;
    public const int Length = 16;

    public uint MessageId => Id;

    public bool IsRequest => Tc1 == 0;

    public static IMavlinkMessage Read(ReadOnlySpan<byte> p) => new TimesyncMessage(
        BinaryPrimitives.ReadInt64LittleEndian(p),
        BinaryPrimitives.ReadInt64LittleEndian(p[8..]));

    public void Write(Span<byte> payload)
    {
        var p = payload;
        BinaryPrimitives.WriteInt64LittleEndian(p, Tc1);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], Ts1);
    }
}
