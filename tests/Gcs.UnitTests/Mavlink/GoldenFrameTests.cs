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
    };

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
