using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Simulation;

namespace Gcs.UnitTests.Mavlink;

public sealed class SimulatedVehicleTests
{
    private static readonly SimulatedVehicleOptions Options = new() { SystemId = 7 };

    [Fact]
    public void Vehicle_stays_on_its_orbit_and_drains_battery()
    {
        var vehicle = new SimulatedVehicle(Options);

        for (var i = 0; i < 600; i++)
        {
            vehicle.Step(TimeSpan.FromMilliseconds(100));
        }

        var position = vehicle.Telemetry().OfType<GlobalPositionIntMessage>().Single();
        var distance = DistanceMetres(Options.CenterLatitude, Options.CenterLongitude, position.LatitudeE7 / 1e7, position.LongitudeE7 / 1e7);
        distance.ShouldBe(Options.OrbitRadiusMetres, tolerance: 1);
        vehicle.BatteryPercent.ShouldBe(100 - (60 * Options.BatteryDrainPercentPerSecond), tolerance: 0.001);
    }

    [Fact]
    public void Reported_heading_matches_the_direction_of_travel()
    {
        var vehicle = new SimulatedVehicle(Options);
        vehicle.Step(TimeSpan.FromSeconds(3));

        var position = vehicle.Telemetry().OfType<GlobalPositionIntMessage>().Single();
        var velocityHeading = (Math.Atan2(position.VelocityEastCmPerSecond, position.VelocityNorthCmPerSecond) * 180 / Math.PI + 360) % 360;

        (position.HeadingCentidegrees / 100.0).ShouldBe(velocityHeading, tolerance: 1);
    }

    [Fact]
    public void Timesync_requests_are_answered_with_the_requester_timestamp_echoed()
    {
        var vehicle = new SimulatedVehicle(Options);

        var answer = vehicle.Handle(new TimesyncMessage(0, 123_456_789)).ShouldBeOfType<TimesyncMessage>();

        answer.Ts1.ShouldBe(123_456_789);
        answer.IsRequest.ShouldBeFalse();
        vehicle.Handle(new TimesyncMessage(5, 6)).ShouldBeNull(); // an answer is not answered again
    }

    [Fact]
    public void The_simulated_radio_weakens_with_distance_from_home()
    {
        var vehicle = new SimulatedVehicle(Options with { StartAirborne = false });
        var atHome = vehicle.RadioStatus().Rssi;

        var far = new SimulatedVehicle(Options with { OrbitRadiusMetres = 1200 }).RadioStatus().Rssi;

        atHome.ShouldBe((byte)200);
        far.ShouldBe((byte)80);
    }

    [Fact]
    public void Disarm_in_flight_is_denied_and_commands_for_other_systems_are_ignored()
    {
        var vehicle = new SimulatedVehicle(Options);

        var disarm = vehicle.Handle(Command(MavCmd.ComponentArmDisarm, param1: 0));
        var notForMe = vehicle.Handle(new CommandLongMessage(8, 1, MavCmd.ComponentArmDisarm, 0, Param1: 0));
        var unknown = vehicle.Handle(Command(MavCmd.DoChangeSpeed));

        disarm.ShouldBe(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Denied));
        vehicle.IsArmed.ShouldBeTrue();
        notForMe.ShouldBeNull();
        unknown.ShouldBe(new CommandAckMessage(MavCmd.DoChangeSpeed, MavResult.Unsupported));
    }

    [Fact]
    public void A_full_flight_arm_takeoff_return_and_automatic_disarm_after_landing()
    {
        var vehicle = new SimulatedVehicle(Options with { StartAirborne = false });
        vehicle.Phase.ShouldBe(SimulatedFlightPhase.OnGround);
        vehicle.Heartbeat().SystemStatus.ShouldBe(MavState.Standby);

        // TAKEOFF before ARM is refused, as on a real autopilot.
        Ack(vehicle, Command(MavCmd.NavTakeoff, param7: (float)(Options.HomeAltitudeMslMetres + 30))).ShouldBe(MavResult.Denied);
        Ack(vehicle, Command(MavCmd.ComponentArmDisarm, param1: 1)).ShouldBe(MavResult.Accepted);
        Ack(vehicle, Command(MavCmd.NavTakeoff, param7: (float)(Options.HomeAltitudeMslMetres + 30))).ShouldBe(MavResult.Accepted);
        vehicle.Heartbeat().CustomMode.ShouldBe(Px4Mode(4, 2)); // AUTO.TAKEOFF

        Run(vehicle, TimeSpan.FromSeconds(15));
        vehicle.Phase.ShouldBe(SimulatedFlightPhase.Hovering);
        vehicle.RelativeAltitudeMetres.ShouldBe(30);
        vehicle.Heartbeat().CustomMode.ShouldBe(Px4Mode(4, 3)); // AUTO.LOITER

        Ack(vehicle, Command(MavCmd.NavReturnToLaunch)).ShouldBe(MavResult.Accepted);
        vehicle.Heartbeat().CustomMode.ShouldBe(Px4Mode(4, 5)); // AUTO.RTL
        Run(vehicle, TimeSpan.FromSeconds(30));

        vehicle.Phase.ShouldBe(SimulatedFlightPhase.OnGround);
        vehicle.RelativeAltitudeMetres.ShouldBe(0);
        vehicle.IsArmed.ShouldBeFalse();
    }

    [Fact]
    public void Land_from_the_orbit_descends_and_set_mode_changes_the_reported_mode()
    {
        var vehicle = new SimulatedVehicle(Options);

        Ack(vehicle, Command(MavCmd.DoSetMode, param1: 1, param2: 3)).ShouldBe(MavResult.Accepted); // POSCTL
        vehicle.Heartbeat().CustomMode.ShouldBe(Px4Mode(3, 0));
        vehicle.Phase.ShouldBe(SimulatedFlightPhase.Hovering);
        Ack(vehicle, Command(MavCmd.DoSetMode, param1: 1, param2: 42)).ShouldBe(MavResult.Denied);

        Ack(vehicle, Command(MavCmd.NavLand)).ShouldBe(MavResult.Accepted);
        Run(vehicle, TimeSpan.FromSeconds(10));
        vehicle.Telemetry().OfType<GlobalPositionIntMessage>().Single().VelocityDownCmPerSecond.ShouldBe((short)200);

        Run(vehicle, TimeSpan.FromSeconds(60));
        vehicle.Phase.ShouldBe(SimulatedFlightPhase.OnGround);
        vehicle.IsArmed.ShouldBeFalse();
    }

    [Fact]
    public void Injected_faults_drop_or_ignore_commands_but_still_record_them()
    {
        var vehicle = new SimulatedVehicle(Options) { DropNextCommands = 1 };

        vehicle.Handle(Command(MavCmd.NavLand)).ShouldBeNull();
        vehicle.Phase.ShouldBe(SimulatedFlightPhase.Orbiting);
        vehicle.IgnoreCommands = true;
        vehicle.Handle(Command(MavCmd.NavLand)).ShouldBeNull();

        vehicle.CommandsReceived.Count.ShouldBe(2);
        vehicle.Phase.ShouldBe(SimulatedFlightPhase.Orbiting);
    }

    private static CommandLongMessage Command(MavCmd command, float param1 = 0, float param2 = 0, float param7 = 0) =>
        new(Options.SystemId, 1, command, 0, Param1: param1, Param2: param2, Param7: param7);

    private static MavResult Ack(SimulatedVehicle vehicle, CommandLongMessage command) =>
        vehicle.Handle(command).ShouldBeOfType<CommandAckMessage>().Result;

    private static void Run(SimulatedVehicle vehicle, TimeSpan duration)
    {
        for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMilliseconds(100))
        {
            vehicle.Step(TimeSpan.FromMilliseconds(100));
        }
    }

    private static uint Px4Mode(byte main, byte sub) => ((uint)main << 16) | ((uint)sub << 24);

    private static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadius = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + (Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadius * Math.Asin(Math.Sqrt(a));
    }
}
