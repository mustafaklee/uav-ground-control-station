using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Simulation;

public sealed record SimulatedVehicleOptions
{
    public byte SystemId { get; init; } = 1;

    /// <summary>Centre of the circle the vehicle flies (default: Ankara).</summary>
    public double CenterLatitude { get; init; } = 39.925533;

    public double CenterLongitude { get; init; } = 32.866287;

    public double OrbitRadiusMetres { get; init; } = 150;

    public double SpeedMetresPerSecond { get; init; } = 12;

    public double HomeAltitudeMslMetres { get; init; } = 938;

    public double RelativeAltitudeMetres { get; init; } = 100;

    /// <summary>Battery percentage lost per second of flight.</summary>
    public double BatteryDrainPercentPerSecond { get; init; } = 0.05;
}

/// <summary>
/// A pretend PX4 multicopter loitering in a circle. It produces the same messages a real autopilot sends,
/// with physically consistent values (position, velocity, heading and bank angle agree), and answers ARM/DISARM.
/// Pure model: no timers or sockets, so it is deterministic and easy to test.
/// </summary>
public sealed class SimulatedVehicle(SimulatedVehicleOptions options)
{
    private const double MetresPerDegreeLatitude = 111_320;
    private const double Gravity = 9.80665;
    private const uint Px4AutoLoiter = (4u << 16) | (3u << 24);
    private const double FullBatteryVolts = 16.8;
    private const double EmptyBatteryVolts = 13.2;
    private const short CruiseCurrentCentiamps = 1500;
    private const ushort CruiseThrottlePercent = 55;
    private const ushort GpsHorizontalDilution = 80;
    private const byte GpsSatellites = 14;

    private double _angleRadians;
    private double _batteryPercent = 100;
    private TimeSpan _elapsed;

    public SimulatedVehicleOptions Options { get; } = options;

    public bool IsArmed { get; private set; } = true;

    public double BatteryPercent => _batteryPercent;

    /// <summary>Advances the simulation by <paramref name="delta"/>.</summary>
    public void Step(TimeSpan delta)
    {
        _elapsed += delta;
        var angularSpeed = Options.SpeedMetresPerSecond / Options.OrbitRadiusMetres;
        _angleRadians = (_angleRadians + (angularSpeed * delta.TotalSeconds)) % (2 * Math.PI);
        if (IsArmed)
        {
            _batteryPercent = Math.Max(0, _batteryPercent - (Options.BatteryDrainPercentPerSecond * delta.TotalSeconds));
        }
    }

    public HeartbeatMessage Heartbeat() => new(
        MavType.Quadrotor,
        MavAutopilot.Px4,
        MavBaseMode.CustomModeEnabled | (IsArmed ? MavBaseMode.SafetyArmed : MavBaseMode.None),
        Px4AutoLoiter,
        MavState.Active);

    /// <summary>The high-rate telemetry set a real autopilot streams (position, attitude, HUD, GPS, battery).</summary>
    public IReadOnlyList<IMavlinkMessage> Telemetry()
    {
        var timeBootMs = (uint)_elapsed.TotalMilliseconds;
        var (north, east) = (Options.OrbitRadiusMetres * Math.Cos(_angleRadians), Options.OrbitRadiusMetres * Math.Sin(_angleRadians));
        var latitude = Options.CenterLatitude + (north / MetresPerDegreeLatitude);
        var longitude = Options.CenterLongitude + (east / (MetresPerDegreeLatitude * Math.Cos(Options.CenterLatitude * Math.PI / 180)));

        // Velocity is the derivative of the circle: tangent to it, clockwise seen from above.
        var angularSpeed = Options.SpeedMetresPerSecond / Options.OrbitRadiusMetres;
        var velocityNorth = -Options.OrbitRadiusMetres * angularSpeed * Math.Sin(_angleRadians);
        var velocityEast = Options.OrbitRadiusMetres * angularSpeed * Math.Cos(_angleRadians);
        var headingDegrees = ((Math.Atan2(velocityEast, velocityNorth) * 180 / Math.PI) + 360) % 360;
        var bankRadians = Math.Atan(Options.SpeedMetresPerSecond * Options.SpeedMetresPerSecond / (Options.OrbitRadiusMetres * Gravity));

        var altitudeMsl = Options.HomeAltitudeMslMetres + Options.RelativeAltitudeMetres;
        var latE7 = (int)Math.Round(latitude * 1e7);
        var lonE7 = (int)Math.Round(longitude * 1e7);
        var voltage = EmptyBatteryVolts + ((FullBatteryVolts - EmptyBatteryVolts) * _batteryPercent / 100);

        return
        [
            new GlobalPositionIntMessage(
                timeBootMs, latE7, lonE7,
                (int)(altitudeMsl * 1000), (int)(Options.RelativeAltitudeMetres * 1000),
                (short)(velocityNorth * 100), (short)(velocityEast * 100), 0,
                (ushort)(headingDegrees * 100)),
            new AttitudeMessage(timeBootMs, (float)bankRadians, 0.035f, (float)(headingDegrees * Math.PI / 180), 0, 0, (float)angularSpeed),
            new VfrHudMessage(
                (float)Options.SpeedMetresPerSecond, (float)Options.SpeedMetresPerSecond, (short)headingDegrees,
                IsArmed ? CruiseThrottlePercent : (ushort)0, (float)altitudeMsl, 0f),
            new GpsRawIntMessage(
                (ulong)_elapsed.TotalMicroseconds, GpsFixType.Fix3D, latE7, lonE7, (int)(altitudeMsl * 1000),
                GpsHorizontalDilution, GpsHorizontalDilution, (ushort)(Options.SpeedMetresPerSecond * 100),
                (ushort)(headingDegrees * 100), GpsSatellites),
            new SysStatusMessage(0, 0, 0, 250, (ushort)(voltage * 1000), IsArmed ? CruiseCurrentCentiamps : (short)0, (sbyte)Math.Round(_batteryPercent)),
        ];
    }

    /// <summary>Handles a message from the GCS and returns the reply, if any.</summary>
    public IMavlinkMessage? Handle(IMavlinkMessage message)
    {
        if (message is not CommandLongMessage command || command.TargetSystem != Options.SystemId)
        {
            return null;
        }

        if (command.Command != MavCmd.ComponentArmDisarm)
        {
            return new CommandAckMessage(command.Command, MavResult.Unsupported);
        }

        IsArmed = command.Param1 >= 0.5f;
        return new CommandAckMessage(command.Command, MavResult.Accepted);
    }
}
