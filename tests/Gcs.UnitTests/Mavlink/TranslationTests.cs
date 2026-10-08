using Gcs.Domain.Telemetry;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Translation;

namespace Gcs.UnitTests.Mavlink;

public sealed class TranslationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MavAutopilot.Px4, MavType.Quadrotor, (4u << 16) | (3u << 24), "AUTO.LOITER")]
    [InlineData(MavAutopilot.Px4, MavType.Quadrotor, (4u << 16) | (4u << 24), "AUTO.MISSION")]
    [InlineData(MavAutopilot.Px4, MavType.Quadrotor, 3u << 16, "POSCTL")]
    [InlineData(MavAutopilot.ArduPilotMega, MavType.Quadrotor, 5u, "LOITER")]
    [InlineData(MavAutopilot.ArduPilotMega, MavType.FixedWing, 5u, "FBWA")]
    [InlineData(MavAutopilot.ArduPilotMega, MavType.VtolTiltrotor, 19u, "QLOITER")]
    [InlineData(MavAutopilot.ArduPilotMega, MavType.Quadrotor, 999u, FlightModeDecoder.Unknown)]
    [InlineData(MavAutopilot.Generic, MavType.Quadrotor, 1u, FlightModeDecoder.Unknown)]
    public void Custom_mode_is_decoded_per_autopilot(MavAutopilot autopilot, MavType type, uint customMode, string expected)
    {
        FlightModeDecoder.Decode(autopilot, type, MavBaseMode.CustomModeEnabled, customMode).ShouldBe(expected);
    }

    [Fact]
    public void Custom_mode_is_ignored_when_the_vehicle_says_it_is_not_in_use()
    {
        FlightModeDecoder.Decode(MavAutopilot.Px4, MavType.Quadrotor, MavBaseMode.None, 3u << 16).ShouldBe(FlightModeDecoder.Unknown);
    }

    [Fact]
    public void Global_position_is_converted_to_degrees_and_metres()
    {
        var message = new GlobalPositionIntMessage(0, 399250000, 328540000, 950_000, 50_000, 0, 0, 0, 9000);

        var update = TelemetryTranslator.Translate(message, Now)!;

        update.Position.ShouldBe(new GeoPosition(39.925, 32.854, 950, 50));
    }

    [Fact]
    public void Attitude_is_converted_to_degrees_with_heading_between_0_and_360()
    {
        var message = new AttitudeMessage(0, (float)(Math.PI / 6), 0, (float)(-Math.PI / 2), 0, 0, 0);

        var attitude = TelemetryTranslator.Translate(message, Now)!.Attitude!;

        attitude.Roll.ShouldBe(30, tolerance: 0.001);
        attitude.Yaw.ShouldBe(270, tolerance: 0.001);
    }

    [Fact]
    public void Nan_air_speed_from_a_vehicle_without_an_air_speed_sensor_becomes_null()
    {
        // What PX4 SITL sends for a quadcopter: no air speed sensor, so air speed is NaN.
        var message = new VfrHudMessage(float.NaN, 4.5f, 90, 40, 950f, 1.25f);

        var motion = TelemetryTranslator.Translate(message, Now)!.Motion!;

        motion.ShouldBe(new MotionState(4.5, null, 1.25, 90));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Messages_whose_values_are_not_numbers_yet_carry_no_telemetry(float value)
    {
        // NaN or infinity cannot be serialized to JSON; a half-initialized estimator's message is dropped instead.
        TelemetryTranslator.Translate(new AttitudeMessage(0, value, 0, 0, 0, 0, 0), Now).ShouldBeNull();
        TelemetryTranslator.Translate(new VfrHudMessage(0, value, 0, 0, 0, 0), Now).ShouldBeNull();
    }

    [Fact]
    public void Heartbeat_becomes_arm_state_and_flight_mode()
    {
        var message = new HeartbeatMessage(MavType.Quadrotor, MavAutopilot.Px4,
            MavBaseMode.SafetyArmed | MavBaseMode.CustomModeEnabled, 3u << 16, MavState.Active);

        TelemetryTranslator.Translate(message, Now)!.Flight.ShouldBe(new FlightState(Armed: true, "POSCTL"));
    }

    [Fact]
    public void Battery_values_marked_unknown_become_null()
    {
        var known = TelemetryTranslator.Translate(new SysStatusMessage(0, 0, 0, 0, 15800, 1234, 76), Now)!.Battery!;
        var unknown = TelemetryTranslator.Translate(new SysStatusMessage(0, 0, 0, 0, ushort.MaxValue, -1, -1), Now)!.Battery!;

        known.ShouldBe(new BatteryState(15.8, 12.34, 76));
        unknown.ShouldBe(new BatteryState(null, null, null));
    }

    [Fact]
    public void Commands_carry_no_telemetry()
    {
        TelemetryTranslator.Translate(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted), Now).ShouldBeNull();
    }
}
