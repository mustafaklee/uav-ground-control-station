using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.UnitTests.Mavlink;

/// <summary>Real links deliver partial frames, several frames at once, noise and corrupted bytes. The parser must cope.</summary>
public sealed class FrameParserTests
{
    [Fact]
    public void Several_frames_in_one_datagram_are_all_returned()
    {
        var parser = new MavlinkFrameParser();

        var frames = parser.Parse([.. GoldenFrames.Heartbeat, .. GoldenFrames.Attitude, .. GoldenFrames.VfrHud]);

        frames.Select(f => f.MessageId).ShouldBe([HeartbeatMessage.Id, AttitudeMessage.Id, VfrHudMessage.Id]);
    }

    [Fact]
    public void A_frame_split_into_single_bytes_is_reassembled()
    {
        var parser = new MavlinkFrameParser();
        var frames = new List<MavlinkFrame>();

        foreach (var value in GoldenFrames.GlobalPositionInt)
        {
            frames.AddRange(parser.Parse([value]));
        }

        frames.ShouldHaveSingleItem().MessageId.ShouldBe(GlobalPositionIntMessage.Id);
    }

    [Fact]
    public void Noise_before_and_between_frames_is_skipped_and_counted()
    {
        var parser = new MavlinkFrameParser();
        byte[] noise = [0x00, 0x13, 0x37, 0x42];

        var frames = parser.Parse([.. noise, .. GoldenFrames.Heartbeat, .. noise, .. GoldenFrames.Heartbeat]);

        frames.Count.ShouldBe(2);
        parser.Statistics.BytesDiscarded.ShouldBe(noise.Length * 2);
    }

    [Fact]
    public void Frame_with_a_corrupted_byte_is_rejected_and_the_next_frame_still_parses()
    {
        var parser = new MavlinkFrameParser();
        var corrupted = (byte[])GoldenFrames.Attitude.Clone();
        corrupted[15] ^= 0xFF; // flip bits inside the payload

        var frames = parser.Parse([.. corrupted, .. GoldenFrames.Heartbeat]);

        frames.ShouldHaveSingleItem().MessageId.ShouldBe(HeartbeatMessage.Id);
        parser.Statistics.CrcErrors.ShouldBe(1);
    }

    [Fact]
    public void Unknown_message_ids_are_skipped_without_losing_sync()
    {
        var parser = new MavlinkFrameParser();
        var unknown = (byte[])GoldenFrames.Attitude.Clone();
        unknown[7] = 0xEE; // message id 238, not registered

        var frames = parser.Parse([.. unknown, .. GoldenFrames.Heartbeat]);

        frames.ShouldHaveSingleItem().MessageId.ShouldBe(HeartbeatMessage.Id);
        parser.Statistics.UnknownMessages.ShouldBe(1);
    }

    [Fact]
    public void Gaps_in_sequence_numbers_are_counted_as_lost_frames()
    {
        var parser = new MavlinkFrameParser();
        var heartbeat = new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.Px4, MavBaseMode.None, 0, MavState.Active);

        foreach (byte sequence in new byte[] { 254, 255, 0, 3 })
        {
            parser.Parse(MavlinkCodec.Encode(heartbeat, sequence, systemId: 1, componentId: 1));
        }

        // 254 → 255 → 0 is continuous (wrap-around); 0 → 3 lost frames 1 and 2.
        parser.Statistics.FramesReceived.ShouldBe(4);
        parser.Statistics.FramesLost.ShouldBe(2);
        parser.Statistics.PacketLossRatio.ShouldBe(2 / 6.0);
    }

    [Fact]
    public void Skipped_unknown_messages_do_not_count_as_lost_frames()
    {
        // A real PX4 interleaves many message types we do not decode. Their sequence numbers must still be tracked,
        // otherwise every skipped frame shows up as a gap (PX4 SITL showed 76 % "loss" on a perfect link).
        var parser = new MavlinkFrameParser();
        var heartbeat = new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.Px4, MavBaseMode.None, 0, MavState.Active);
        var unknown = MavlinkCodec.Encode(heartbeat, sequence: 1, systemId: 1, componentId: 1);
        unknown[7] = 0xEE; // message id 238, not registered

        parser.Parse([
            .. MavlinkCodec.Encode(heartbeat, sequence: 0, systemId: 1, componentId: 1),
            .. unknown,
            .. MavlinkCodec.Encode(heartbeat, sequence: 2, systemId: 1, componentId: 1),
        ]);

        parser.Statistics.FramesReceived.ShouldBe(2);
        parser.Statistics.UnknownMessages.ShouldBe(1);
        parser.Statistics.FramesLost.ShouldBe(0);
        parser.Statistics.PacketLossRatio.ShouldBe(0);
    }

    [Fact]
    public void Mavlink_v1_frames_are_accepted()
    {
        // HEARTBEAT as MAVLink 1: STX 0xFE, len, seq, sys, comp, msgid, 9-byte payload, CRC.
        var payload = new byte[HeartbeatMessage.Length];
        new HeartbeatMessage(MavType.FixedWing, MavAutopilot.ArduPilotMega, MavBaseMode.None, 10, MavState.Standby).Write(payload);
        byte[] header = [MavlinkWire.StartV1, (byte)payload.Length, 7, 42, 1, 0];
        var crc = MavlinkCrc.Compute([.. header[1..], .. payload], crcExtra: 50);
        byte[] frame = [.. header, .. payload, (byte)(crc & 0xFF), (byte)(crc >> 8)];

        var parsed = new MavlinkFrameParser().Parse(frame).ShouldHaveSingleItem();
        MavlinkCodec.TryDecode(parsed, out var message);

        parsed.Version.ShouldBe(MavlinkVersion.V1);
        parsed.SystemId.ShouldBe((byte)42);
        message.ShouldBeOfType<HeartbeatMessage>().Autopilot.ShouldBe(MavAutopilot.ArduPilotMega);
    }

    [Fact]
    public void All_zero_payload_keeps_one_byte_when_encoded()
    {
        var frame = MavlinkCodec.Encode(new CommandAckMessage(0, MavResult.Accepted), 0, 1, 1);

        frame[1].ShouldBe((byte)1);
        new MavlinkFrameParser().Parse(frame).ShouldHaveSingleItem();
    }
}
