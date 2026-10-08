using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.UnitTests.Mavlink;

/// <summary>
/// Our codec against the reference implementation: every frame in <see cref="GoldenFrames"/> was produced by pymavlink
/// (scripts/generate-mavlink-golden.py). Encoding must produce the identical bytes and decoding must restore the values.
/// </summary>
public sealed class GoldenFrameTests
{
    private const byte SystemId = 1;
    private const byte ComponentId = 1;

    public static TheoryData<string, byte, IMavlinkMessage, byte[]> Cases => new()
    {
        {
            "HEARTBEAT (PX4 quad, armed, AUTO.LOITER)", 0,
            new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.Px4, MavBaseMode.SafetyArmed | MavBaseMode.CustomModeEnabled,
                (4u << 16) | (3u << 24), MavState.Active),
            GoldenFrames.Heartbeat
        },
        {
            "HEARTBEAT (GCS)", 1,
            new HeartbeatMessage(MavType.Gcs, MavAutopilot.Invalid, MavBaseMode.None, 0, MavState.Active),
            GoldenFrames.HeartbeatGcs
        },
        { "SYS_STATUS", 2, new SysStatusMessage(0, 0, 0, 250, 15800, 1234, 76), GoldenFrames.SysStatus },
        {
            "GPS_RAW_INT", 3,
            new GpsRawIntMessage(123456789, GpsFixType.Fix3D, 399250000, 328540000, 950000, 120, 180, 1250, 9000, 14),
            GoldenFrames.GpsRawInt
        },
        {
            "ATTITUDE", 4,
            new AttitudeMessage(65000, 0.1f, -0.05f, 1.5707964f, 0.01f, 0.02f, -0.03f),
            GoldenFrames.Attitude
        },
        {
            "GLOBAL_POSITION_INT", 5,
            new GlobalPositionIntMessage(65000, 399250000, 328540000, 950000, 50000, 1200, -300, -50, 9000),
            GoldenFrames.GlobalPositionInt
        },
        { "VFR_HUD", 6, new VfrHudMessage(13.5f, 12.25f, 90, 55, 950f, -0.5f), GoldenFrames.VfrHud },
        {
            "COMMAND_LONG (arm)", 7,
            new CommandLongMessage(1, 1, MavCmd.ComponentArmDisarm, 0, Param1: 1),
            GoldenFrames.CommandLong
        },
        { "COMMAND_ACK (accepted)", 8, new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted), GoldenFrames.CommandAck },
        { "MISSION_REQUEST_LIST", 9, new MissionRequestListMessage(1, 1), GoldenFrames.MissionRequestList },
        { "MISSION_COUNT", 10, new MissionCountMessage(1, 1, 5), GoldenFrames.MissionCount },
        { "MISSION_REQUEST_INT", 11, new MissionRequestIntMessage(255, 190, 3), GoldenFrames.MissionRequestInt },
        {
            "MISSION_ITEM_INT (waypoint, yaw unset)", 12,
            new MissionItemIntMessage(1, 1, 2, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0, 1,
                5f, 2f, 0f, PythonNaN, 399255330, 328662870, 120f),
            GoldenFrames.MissionItemInt
        },
        { "MISSION_ACK (accepted)", 13, new MissionAckMessage(255, 190, MavMissionResult.Accepted), GoldenFrames.MissionAck },
        { "MISSION_ACK (invalid sequence)", 14, new MissionAckMessage(255, 190, MavMissionResult.InvalidSequence), GoldenFrames.MissionAckInvalidSequence },
        { "MISSION_CURRENT", 15, new MissionCurrentMessage(2), GoldenFrames.MissionCurrent },
        { "MISSION_ITEM_REACHED", 16, new MissionItemReachedMessage(2), GoldenFrames.MissionItemReached },
        { "RADIO_STATUS", 17, new RadioStatusMessage(182, 176, 97, 41, 44, 12, 3), GoldenFrames.RadioStatus },
        { "TIMESYNC (answer)", 18, new TimesyncMessage(1234567890123, 987654321000), GoldenFrames.Timesync },
    };

    /// <summary>
    /// Python packs float('nan') as 0x7FC00000; C#'s float.NaN is 0xFFC00000. Both are valid "unset" values for autopilots,
    /// but a byte-exact comparison needs the same bits.
    /// </summary>
    private static readonly float PythonNaN = BitConverter.Int32BitsToSingle(0x7FC00000);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Encoding_produces_exactly_the_reference_bytes(string name, byte sequence, IMavlinkMessage message, byte[] golden)
    {
        var frame = MavlinkCodec.Encode(message, sequence, SystemId, ComponentId);

        frame.ShouldBe(golden, name);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decoding_the_reference_bytes_restores_the_message(string name, byte sequence, IMavlinkMessage message, byte[] golden)
    {
        var frame = new MavlinkFrameParser().Parse(golden).ShouldHaveSingleItem(name);

        frame.Version.ShouldBe(MavlinkVersion.V2);
        frame.Sequence.ShouldBe(sequence);
        frame.SystemId.ShouldBe(SystemId);
        frame.ComponentId.ShouldBe(ComponentId);
        MavlinkCodec.TryDecode(frame, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(message, name);
    }

    [Fact]
    public void Truncated_payload_is_zero_filled_when_decoding()
    {
        // pymavlink truncated COMMAND_ACK to 2 bytes because result = ACCEPTED (0) is the trailing zero.
        GoldenFrames.CommandAck[1].ShouldBe((byte)2);

        var frame = new MavlinkFrameParser().Parse(GoldenFrames.CommandAck).Single();
        MavlinkCodec.TryDecode(frame, out var decoded);

        decoded.ShouldBe(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));
    }
}
