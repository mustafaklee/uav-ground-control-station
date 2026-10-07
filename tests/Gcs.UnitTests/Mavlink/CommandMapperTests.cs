using Gcs.Domain.Commands;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Translation;

namespace Gcs.UnitTests.Mavlink;

public sealed class CommandMapperTests
{
    private const byte SystemId = 3;

    [Fact]
    public void Arm_and_disarm_use_component_arm_disarm_with_param1()
    {
        Map(VehicleCommand.Arm()).ShouldBe(new CommandLongMessage(SystemId, 1, MavCmd.ComponentArmDisarm, 0, Param1: 1));
        Map(VehicleCommand.Disarm()).ShouldBe(new CommandLongMessage(SystemId, 1, MavCmd.ComponentArmDisarm, 0, Param1: 0));
    }

    [Fact]
    public void Px4_takeoff_altitude_is_above_mean_sea_level_and_ardupilot_takeoff_altitude_is_above_home()
    {
        var takeoff = VehicleCommand.Takeoff(30).Value;

        Map(takeoff, AutopilotType.Px4, home: 938)!.Param7.ShouldBe(968f);
        Map(takeoff, AutopilotType.ArduPilot, home: 938)!.Param7.ShouldBe(30f);
        Map(takeoff, AutopilotType.Px4, home: null).ShouldBeNull();
    }

    [Theory]
    [InlineData(AutopilotType.Px4, VehicleType.Multirotor, "AUTO.LOITER", 4u, 3u)]
    [InlineData(AutopilotType.Px4, VehicleType.Multirotor, "POSCTL", 3u, 0u)]
    [InlineData(AutopilotType.ArduPilot, VehicleType.Multirotor, "LOITER", 5u, 0u)]
    [InlineData(AutopilotType.ArduPilot, VehicleType.FixedWing, "FBWA", 5u, 0u)]
    public void Set_mode_sends_the_autopilots_own_mode_numbers(AutopilotType autopilot, VehicleType type, string mode, uint custom, uint sub)
    {
        var message = CommandMapper.ToCommandLong(VehicleCommand.SetMode(mode).Value, autopilot, type, SystemId, 0, out _)!;

        message.Command.ShouldBe(MavCmd.DoSetMode);
        message.Param1.ShouldBe((float)MavBaseMode.CustomModeEnabled);
        message.Param2.ShouldBe(custom);
        message.Param3.ShouldBe(sub);
    }

    [Fact]
    public void Every_selectable_mode_encodes_and_decodes_back_to_the_same_name()
    {
        foreach (var (autopilot, type) in new[] { (MavAutopilot.Px4, MavType.Quadrotor), (MavAutopilot.ArduPilotMega, MavType.Quadrotor), (MavAutopilot.ArduPilotMega, MavType.FixedWing) })
        {
            foreach (var mode in FlightModeDecoder.SelectableModes(autopilot, type))
            {
                FlightModeDecoder.TryEncode(autopilot, type, mode, out var custom, out var sub).ShouldBeTrue(mode);
                var customMode = autopilot == MavAutopilot.Px4 ? (custom << 16) | (sub << 24) : custom;
                FlightModeDecoder.Decode(autopilot, type, MavBaseMode.CustomModeEnabled, customMode).ShouldBe(mode);
            }
        }
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData("POSCTL.LOITER")]
    [InlineData("AUTO.NOPE")]
    [InlineData("LOITER")]
    public void Unknown_or_malformed_px4_modes_are_not_encoded(string mode) =>
        FlightModeDecoder.TryEncode(MavAutopilot.Px4, MavType.Quadrotor, mode, out _, out _).ShouldBeFalse();

    private static CommandLongMessage? Map(VehicleCommand command, AutopilotType autopilot = AutopilotType.Px4, double? home = 938) =>
        CommandMapper.ToCommandLong(command, autopilot, VehicleType.Multirotor, SystemId, home, out _);
}
