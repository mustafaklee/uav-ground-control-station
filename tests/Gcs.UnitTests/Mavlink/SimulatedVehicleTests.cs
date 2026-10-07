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
    public void Arm_and_disarm_commands_are_acknowledged()
    {
        var vehicle = new SimulatedVehicle(Options);

        var disarm = vehicle.Handle(new CommandLongMessage(7, 1, MavCmd.ComponentArmDisarm, 0, Param1: 0));
        var other = vehicle.Handle(new CommandLongMessage(7, 1, MavCmd.NavTakeoff, 0));
        var notForMe = vehicle.Handle(new CommandLongMessage(8, 1, MavCmd.ComponentArmDisarm, 0, Param1: 1));

        disarm.ShouldBe(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));
        vehicle.IsArmed.ShouldBeFalse();
        vehicle.Heartbeat().IsArmed.ShouldBeFalse();
        other.ShouldBe(new CommandAckMessage(MavCmd.NavTakeoff, MavResult.Unsupported));
        notForMe.ShouldBeNull();
    }

    private static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadius = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + (Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadius * Math.Asin(Math.Sqrt(a));
    }
}
