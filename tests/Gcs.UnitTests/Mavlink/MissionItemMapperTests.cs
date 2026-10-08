using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Translation;

namespace Gcs.UnitTests.Mavlink;

public sealed class MissionItemMapperTests
{
    private static readonly MissionItem[] Plan =
    [
        MissionItem.Create(MissionCommand.Takeoff, altitude: 30).Value,
        MissionItem.Create(MissionCommand.Waypoint, 39.9255331, 32.8662871, 50, holdSeconds: 5, speed: 10).Value,
        MissionItem.Create(MissionCommand.Loiter, 39.9265, 32.8672, 60, holdSeconds: 30).Value,
        MissionItem.Create(MissionCommand.Waypoint, 39.9275, 32.8682, 50, speed: 6).Value,
        MissionItem.Create(MissionCommand.ReturnToLaunch).Value,
    ];

    [Fact]
    public void Px4_items_start_at_sequence_0_with_speed_changes_inserted_only_when_speed_changes()
    {
        var messages = MissionItemMapper.ToMavlink(Plan, AutopilotType.Px4, targetSystem: 1, targetComponent: 1);

        messages.Select(m => m.Command).ShouldBe(
        [
            MavCmd.NavTakeoff, MavCmd.DoChangeSpeed, MavCmd.NavWaypoint, MavCmd.NavLoiterTime,
            MavCmd.DoChangeSpeed, MavCmd.NavWaypoint, MavCmd.NavReturnToLaunch,
        ]);
        messages.Select(m => (int)m.Seq).ShouldBe([0, 1, 2, 3, 4, 5, 6]);
        messages[0].Current.ShouldBe((byte)1);
        messages.Skip(1).ShouldAllBe(m => m.Current == 0);
        messages.ShouldAllBe(m => m.TargetSystem == 1 && m.TargetComponent == 1);
    }

    [Fact]
    public void Positions_are_sent_in_1e7_degrees_with_altitude_relative_to_home()
    {
        var waypoint = MissionItemMapper.ToMavlink(Plan, AutopilotType.Px4, 1, 1)[2];

        waypoint.Frame.ShouldBe(MavFrame.GlobalRelativeAltInt);
        waypoint.X.ShouldBe(399255331);
        waypoint.Y.ShouldBe(328662871);
        waypoint.Z.ShouldBe(50f);
        waypoint.Param1.ShouldBe(5f); // hold time
        float.IsNaN(waypoint.Param4).ShouldBeTrue(); // yaw left to the autopilot
    }

    [Fact]
    public void ArduPilot_gets_a_home_placeholder_at_sequence_0()
    {
        var messages = MissionItemMapper.ToMavlink(Plan, AutopilotType.ArduPilot, 1, 1);

        messages.Count.ShouldBe(8);
        messages[0].Command.ShouldBe(MavCmd.NavWaypoint);
        messages[1].Command.ShouldBe(MavCmd.NavTakeoff);
        messages[1].Seq.ShouldBe((ushort)1);
    }

    [Fact]
    public void Repeating_the_current_speed_is_redundant_and_is_not_sent_again()
    {
        MissionItem[] plan =
        [
            MissionItem.Create(MissionCommand.Takeoff, altitude: 30).Value,
            MissionItem.Create(MissionCommand.Waypoint, 39.9255, 32.8662, 50, speed: 10).Value,
            MissionItem.Create(MissionCommand.Waypoint, 39.9265, 32.8672, 50, speed: 10).Value,
            MissionItem.Create(MissionCommand.Land).Value,
        ];

        var messages = MissionItemMapper.ToMavlink(plan, AutopilotType.Px4, 1, 1);
        var restored = MissionItemMapper.FromMavlink(messages, AutopilotType.Px4);

        messages.Count(m => m.Command == MavCmd.DoChangeSpeed).ShouldBe(1);
        restored[2].Speed.ShouldBeNull(); // same meaning: "keep the current speed" (10 m/s)
    }

    [Theory]
    [InlineData(AutopilotType.Px4)]
    [InlineData(AutopilotType.ArduPilot)]
    public void Round_trip_restores_the_original_plan(AutopilotType autopilot)
    {
        var messages = MissionItemMapper.ToMavlink(Plan, autopilot, 1, 1);

        var items = MissionItemMapper.FromMavlink(messages, autopilot);

        items.ShouldBe(Plan);
    }

    [Fact]
    public void Takeoff_here_gets_the_vehicle_position_and_land_here_the_position_before_it()
    {
        // PX4 reads 0/0 literally; with it, PX4 SITL flew towards 0° N 0° E ("first waypoint 5548 km from home").
        MissionItem[] plan =
        [
            MissionItem.Create(MissionCommand.Takeoff, altitude: 20).Value,
            MissionItem.Create(MissionCommand.Waypoint, 39.9265, 32.8672, 25).Value,
            MissionItem.Create(MissionCommand.Land).Value,
        ];

        var messages = MissionItemMapper.ToMavlink(plan, AutopilotType.Px4, 1, 1, vehiclePosition: (399255330, 328662870));

        (messages[0].X, messages[0].Y).ShouldBe((399255330, 328662870));
        (messages[2].X, messages[2].Y).ShouldBe((399265000, 328672000));
    }

    [Fact]
    public void Only_a_here_item_before_any_position_needs_the_vehicle_position()
    {
        var land = MissionItem.Create(MissionCommand.Land).Value;
        var waypoint = MissionItem.Create(MissionCommand.Waypoint, 39.9265, 32.8672, 25).Value;

        MissionItemMapper.NeedsVehiclePosition(Plan).ShouldBeTrue(); // starts with a takeoff "here"
        MissionItemMapper.NeedsVehiclePosition([waypoint, land]).ShouldBeFalse();
        MissionItemMapper.NeedsVehiclePosition([MissionItem.Create(MissionCommand.ReturnToLaunch).Value, land]).ShouldBeTrue();
    }
}
