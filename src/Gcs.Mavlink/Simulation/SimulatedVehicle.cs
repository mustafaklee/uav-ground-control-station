using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Simulation;

public sealed record SimulatedVehicleOptions
{
    public byte SystemId { get; init; } = 1;

    /// <summary>Centre of the circle the vehicle flies (default: Ankara). Also its home position.</summary>
    public double CenterLatitude { get; init; } = 39.925533;

    public double CenterLongitude { get; init; } = 32.866287;

    public double OrbitRadiusMetres { get; init; } = 150;

    public double SpeedMetresPerSecond { get; init; } = 12;

    public double HomeAltitudeMslMetres { get; init; } = 938;

    public double RelativeAltitudeMetres { get; init; } = 100;

    /// <summary>Battery percentage lost per second of flight (0.02 %/s ≈ 80 minutes from full to empty).</summary>
    public double BatteryDrainPercentPerSecond { get; init; } = 0.02;

    /// <summary>
    /// True (default): the vehicle starts armed and circling at <see cref="RelativeAltitudeMetres"/>, so telemetry is
    /// interesting right away. False: it starts disarmed on the ground at home, ready for ARM and TAKEOFF.
    /// </summary>
    public bool StartAirborne { get; init; } = true;

    public double ClimbRateMetresPerSecond { get; init; } = 3;

    public double DescentRateMetresPerSecond { get; init; } = 2;

    /// <summary>Altitude for a takeoff started by switching to AUTO.TAKEOFF (PX4 calls this MIS_TAKEOFF_ALT).</summary>
    public double DefaultTakeoffAltitudeMetres { get; init; } = 10;
}

/// <summary>What the simulated vehicle is doing.</summary>
public enum SimulatedFlightPhase
{
    OnGround,
    TakingOff,

    /// <summary>Holding position in the air (after takeoff, or after a mode change).</summary>
    Hovering,

    /// <summary>Circling the home position (the airborne start state).</summary>
    Orbiting,

    /// <summary>Flying straight back to home, then landing.</summary>
    Returning,
    Landing,
}

/// <summary>
/// A pretend PX4 multicopter. It produces the same messages a real autopilot sends, with physically consistent values
/// (position, velocity, heading and bank angle agree), and answers commands the way PX4 does:
/// <code>
/// OnGround ──ARM──► (armed) ──TAKEOFF──► TakingOff ──altitude reached──► Hovering
/// Hovering / Orbiting ──LAND──► Landing ──touchdown──► OnGround (auto-disarm)
/// Hovering / Orbiting ──RTL──► Returning ──at home──► Landing
/// DISARM in the air ──► DENIED (it would fall)      TAKEOFF while disarmed ──► DENIED
/// </code>
/// Faults can be injected for tests: <see cref="IgnoreCommands"/> (an autopilot that never answers) and
/// <see cref="DropNextCommands"/> (lost uplink packets). Pure model: no timers or sockets, so it is deterministic.
/// Thread safe: the runner steps it from a timer while its receive loop handles commands.
/// </summary>
public sealed class SimulatedVehicle
{
    private const double MetresPerDegreeLatitude = 111_320;
    private const double Gravity = 9.80665;
    private const double FullBatteryVolts = 16.8;
    private const double EmptyBatteryVolts = 13.2;
    private const short CruiseCurrentCentiamps = 1500;
    private const short IdleCurrentCentiamps = 300;
    private const ushort CruiseThrottlePercent = 55;
    private const ushort IdleThrottlePercent = 10;
    private const ushort GpsHorizontalDilution = 80;
    private const byte GpsSatellites = 14;
    private const byte Px4MainAuto = 4;

    // PX4 custom_mode: main mode in byte 2, AUTO sub mode in byte 3.
    private static readonly uint AutoTakeoff = Px4Mode(Px4MainAuto, 2);
    private static readonly uint AutoLoiter = Px4Mode(Px4MainAuto, 3);
    private static readonly uint AutoRtl = Px4Mode(Px4MainAuto, 5);
    private static readonly uint AutoLand = Px4Mode(Px4MainAuto, 6);
    private static readonly HashSet<byte> Px4MainModes = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly HashSet<byte> Px4AutoSubModes = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    private readonly Lock _gate = new();
    private readonly List<CommandLongMessage> _commandsReceived = [];
    private double _angleRadians;
    private double _north;
    private double _east;
    private double _relativeAltitude;
    private double _targetAltitude;
    private double _headingDegrees;
    private double _batteryPercent = 100;
    private uint _customMode = AutoLoiter;
    private TimeSpan _elapsed;
    private List<MissionItemIntMessage> _mission = [];
    private List<MissionItemIntMessage>? _incoming;
    private int _incomingCount;

    public SimulatedVehicle(SimulatedVehicleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        if (options.StartAirborne)
        {
            Phase = SimulatedFlightPhase.Orbiting;
            IsArmed = true;
            _relativeAltitude = options.RelativeAltitudeMetres;
            (_north, _east) = OrbitPoint(0);
        }
        else
        {
            Phase = SimulatedFlightPhase.OnGround;
        }
    }

    public SimulatedVehicleOptions Options { get; }

    public bool IsArmed { get; private set; }

    public SimulatedFlightPhase Phase { get; private set; }

    public double RelativeAltitudeMetres
    {
        get
        {
            lock (_gate)
            {
                return _relativeAltitude;
            }
        }
    }

    public double BatteryPercent => _batteryPercent;

    /// <summary>When true, COMMAND_LONG is received but never executed or answered (a hung or busy autopilot).</summary>
    public bool IgnoreCommands { get; set; }

    /// <summary>The next N COMMAND_LONG messages are lost on the way, as on a lossy radio link.</summary>
    public int DropNextCommands { get; set; }

    /// <summary>Every COMMAND_LONG that reached the vehicle, including ignored and dropped ones (for tests).</summary>
    public IReadOnlyList<CommandLongMessage> CommandsReceived
    {
        get
        {
            lock (_gate)
            {
                return [.. _commandsReceived];
            }
        }
    }

    /// <summary>Advances the simulation by <paramref name="delta"/>.</summary>
    public void Step(TimeSpan delta)
    {
        lock (_gate)
        {
            _elapsed += delta;
            var seconds = delta.TotalSeconds;
            switch (Phase)
            {
                case SimulatedFlightPhase.Orbiting:
                    _angleRadians = (_angleRadians + (Options.SpeedMetresPerSecond / Options.OrbitRadiusMetres * seconds)) % (2 * Math.PI);
                    (_north, _east) = OrbitPoint(_angleRadians);
                    break;
                case SimulatedFlightPhase.TakingOff:
                    _relativeAltitude = Math.Min(_targetAltitude, _relativeAltitude + (Options.ClimbRateMetresPerSecond * seconds));
                    if (_relativeAltitude >= _targetAltitude)
                    {
                        Phase = SimulatedFlightPhase.Hovering;
                        _customMode = AutoLoiter;
                    }

                    break;
                case SimulatedFlightPhase.Returning:
                    var distance = Math.Sqrt((_north * _north) + (_east * _east));
                    var travel = Options.SpeedMetresPerSecond * seconds;
                    if (distance <= travel)
                    {
                        (_north, _east) = (0, 0);
                        Phase = SimulatedFlightPhase.Landing; // PX4 lands at home at the end of RTL; the mode stays AUTO.RTL
                    }
                    else
                    {
                        _north -= _north / distance * travel;
                        _east -= _east / distance * travel;
                    }

                    break;
                case SimulatedFlightPhase.Landing:
                    _relativeAltitude = Math.Max(0, _relativeAltitude - (Options.DescentRateMetresPerSecond * seconds));
                    if (_relativeAltitude <= 0)
                    {
                        Phase = SimulatedFlightPhase.OnGround;
                        IsArmed = false; // autopilots disarm on their own shortly after touchdown
                    }

                    break;
                default:
                    break;
            }

            if (IsArmed)
            {
                _batteryPercent = Math.Max(0, _batteryPercent - (Options.BatteryDrainPercentPerSecond * seconds));
            }
        }
    }

    public HeartbeatMessage Heartbeat()
    {
        lock (_gate)
        {
            return new HeartbeatMessage(
                MavType.Quadrotor,
                MavAutopilot.Px4,
                MavBaseMode.CustomModeEnabled | (IsArmed ? MavBaseMode.SafetyArmed : MavBaseMode.None),
                _customMode,
                Phase == SimulatedFlightPhase.OnGround ? MavState.Standby : MavState.Active);
        }
    }

    /// <summary>The high-rate telemetry set a real autopilot streams (position, attitude, HUD, GPS, battery).</summary>
    public IReadOnlyList<IMavlinkMessage> Telemetry()
    {
        lock (_gate)
        {
            var timeBootMs = (uint)_elapsed.TotalMilliseconds;
            var latitude = Options.CenterLatitude + (_north / MetresPerDegreeLatitude);
            var longitude = Options.CenterLongitude + (_east / (MetresPerDegreeLatitude * Math.Cos(Options.CenterLatitude * Math.PI / 180)));

            var (velocityNorth, velocityEast, climb, bankRadians) = Motion();
            var groundSpeed = Math.Sqrt((velocityNorth * velocityNorth) + (velocityEast * velocityEast));
            if (groundSpeed > 0.01)
            {
                _headingDegrees = ((Math.Atan2(velocityEast, velocityNorth) * 180 / Math.PI) + 360) % 360;
            }

            var altitudeMsl = Options.HomeAltitudeMslMetres + _relativeAltitude;
            var latE7 = (int)Math.Round(latitude * 1e7);
            var lonE7 = (int)Math.Round(longitude * 1e7);
            var voltage = EmptyBatteryVolts + ((FullBatteryVolts - EmptyBatteryVolts) * _batteryPercent / 100);
            var airborne = Phase != SimulatedFlightPhase.OnGround;
            var throttle = !IsArmed ? (ushort)0 : airborne ? CruiseThrottlePercent : IdleThrottlePercent;
            var current = !IsArmed ? (short)0 : airborne ? CruiseCurrentCentiamps : IdleCurrentCentiamps;
            var angularSpeed = Phase == SimulatedFlightPhase.Orbiting ? Options.SpeedMetresPerSecond / Options.OrbitRadiusMetres : 0;

            return
            [
                new GlobalPositionIntMessage(
                    timeBootMs, latE7, lonE7,
                    (int)Math.Round(altitudeMsl * 1000), (int)Math.Round(_relativeAltitude * 1000),
                    (short)(velocityNorth * 100), (short)(velocityEast * 100), (short)(-climb * 100),
                    (ushort)(_headingDegrees * 100)),
                new AttitudeMessage(
                    timeBootMs, (float)bankRadians, airborne ? 0.035f : 0f, (float)(_headingDegrees * Math.PI / 180), 0, 0, (float)angularSpeed),
                new VfrHudMessage((float)groundSpeed, (float)groundSpeed, (short)_headingDegrees, throttle, (float)altitudeMsl, (float)climb),
                new GpsRawIntMessage(
                    (ulong)_elapsed.TotalMicroseconds, GpsFixType.Fix3D, latE7, lonE7, (int)Math.Round(altitudeMsl * 1000),
                    GpsHorizontalDilution, GpsHorizontalDilution, (ushort)(groundSpeed * 100),
                    (ushort)(_headingDegrees * 100), GpsSatellites),
                new SysStatusMessage(0, 0, 0, 250, (ushort)(voltage * 1000), current, (sbyte)Math.Round(_batteryPercent)),
            ];
        }
    }

    /// <summary>The mission currently stored on the vehicle (as uploaded by a GCS).</summary>
    public IReadOnlyList<MissionItemIntMessage> Mission => _mission;

    /// <summary>Handles a message from the GCS and returns the reply, if any.</summary>
    /// <param name="message">The decoded message.</param>
    /// <param name="senderSystem">System id of the GCS that sent it; replies are addressed back to it.</param>
    /// <param name="senderComponent">Component id of the sender.</param>
    public IMavlinkMessage? Handle(IMavlinkMessage message, byte senderSystem = 255, byte senderComponent = MavComponent.MissionPlanner)
    {
        lock (_gate)
        {
            return message switch
            {
                CommandLongMessage command when command.TargetSystem == Options.SystemId => ReceiveCommand(command),
                MissionCountMessage count when count.TargetSystem == Options.SystemId => StartMissionUpload(count, senderSystem, senderComponent),
                MissionItemIntMessage item when item.TargetSystem == Options.SystemId => ReceiveMissionItem(item, senderSystem, senderComponent),
                MissionRequestListMessage list when list.TargetSystem == Options.SystemId =>
                    new MissionCountMessage(senderSystem, senderComponent, (ushort)_mission.Count),
                MissionRequestIntMessage request when request.TargetSystem == Options.SystemId && request.Seq < _mission.Count =>
                    _mission[request.Seq] with { TargetSystem = senderSystem, TargetComponent = senderComponent },
                _ => null,
            };
        }
    }

    private static uint Px4Mode(byte main, byte sub) => ((uint)main << 16) | ((uint)sub << 24);

    private (double North, double East) OrbitPoint(double angle) =>
        (Options.OrbitRadiusMetres * Math.Cos(angle), Options.OrbitRadiusMetres * Math.Sin(angle));

    /// <summary>Velocity north/east and climb in m/s, bank angle in radians, for the current phase.</summary>
    private (double North, double East, double Climb, double Bank) Motion()
    {
        switch (Phase)
        {
            case SimulatedFlightPhase.Orbiting:
                // The derivative of the circle: tangent to it, clockwise seen from above.
                var angularSpeed = Options.SpeedMetresPerSecond / Options.OrbitRadiusMetres;
                var bank = Math.Atan(Options.SpeedMetresPerSecond * Options.SpeedMetresPerSecond / (Options.OrbitRadiusMetres * Gravity));
                return (-Options.OrbitRadiusMetres * angularSpeed * Math.Sin(_angleRadians),
                    Options.OrbitRadiusMetres * angularSpeed * Math.Cos(_angleRadians), 0, bank);
            case SimulatedFlightPhase.Returning:
                var distance = Math.Sqrt((_north * _north) + (_east * _east));
                return distance < 0.01
                    ? (0, 0, 0, 0)
                    : (-_north / distance * Options.SpeedMetresPerSecond, -_east / distance * Options.SpeedMetresPerSecond, 0, 0);
            case SimulatedFlightPhase.TakingOff:
                return (0, 0, Options.ClimbRateMetresPerSecond, 0);
            case SimulatedFlightPhase.Landing:
                return (0, 0, -Options.DescentRateMetresPerSecond, 0);
            default:
                return (0, 0, 0, 0);
        }
    }

    private CommandAckMessage? ReceiveCommand(CommandLongMessage command)
    {
        _commandsReceived.Add(command);
        if (DropNextCommands > 0)
        {
            DropNextCommands--;
            return null;
        }

        return IgnoreCommands ? null : new CommandAckMessage(command.Command, Execute(command));
    }

    private MavResult Execute(CommandLongMessage command) => command.Command switch
    {
        MavCmd.ComponentArmDisarm when command.Param1 >= 0.5f => Arm(),
        MavCmd.ComponentArmDisarm => Disarm(),
        MavCmd.NavTakeoff => Takeoff(command.Param7 - Options.HomeAltitudeMslMetres), // PX4: param7 is AMSL
        MavCmd.NavLand => Land(),
        MavCmd.NavReturnToLaunch => ReturnToLaunch(),
        MavCmd.DoSetMode => SetMode((byte)command.Param2, (byte)command.Param3),
        _ => MavResult.Unsupported,
    };

    private MavResult Arm()
    {
        IsArmed = true; // re-arming an armed vehicle is accepted, as on PX4
        return MavResult.Accepted;
    }

    private MavResult Disarm()
    {
        if (IsArmed && Phase != SimulatedFlightPhase.OnGround)
        {
            return MavResult.Denied;
        }

        IsArmed = false;
        return MavResult.Accepted;
    }

    private MavResult Takeoff(double relativeAltitude)
    {
        if (!IsArmed || Phase != SimulatedFlightPhase.OnGround || !double.IsFinite(relativeAltitude) || relativeAltitude < 1)
        {
            return MavResult.Denied;
        }

        _targetAltitude = relativeAltitude;
        Phase = SimulatedFlightPhase.TakingOff;
        _customMode = AutoTakeoff;
        return MavResult.Accepted;
    }

    private MavResult Land()
    {
        if (Phase != SimulatedFlightPhase.OnGround)
        {
            Phase = SimulatedFlightPhase.Landing;
            _customMode = AutoLand;
        }

        return MavResult.Accepted;
    }

    private MavResult ReturnToLaunch()
    {
        if (Phase == SimulatedFlightPhase.OnGround)
        {
            return MavResult.Denied; // already home
        }

        Phase = SimulatedFlightPhase.Returning;
        _customMode = AutoRtl;
        return MavResult.Accepted;
    }

    private MavResult SetMode(byte main, byte sub)
    {
        if (!Px4MainModes.Contains(main) || (main == Px4MainAuto && !Px4AutoSubModes.Contains(sub)))
        {
            return MavResult.Denied;
        }

        var mode = Px4Mode(main, main == Px4MainAuto ? sub : (byte)0);
        if (mode == AutoTakeoff)
        {
            return Takeoff(Options.DefaultTakeoffAltitudeMetres);
        }

        if (mode == AutoLand)
        {
            return Land();
        }

        if (mode == AutoRtl)
        {
            return ReturnToLaunch();
        }

        // Every other mode holds position in this simple model (no stick input, no mission flying).
        if (Phase is SimulatedFlightPhase.Orbiting or SimulatedFlightPhase.Returning or SimulatedFlightPhase.Landing or SimulatedFlightPhase.TakingOff)
        {
            Phase = SimulatedFlightPhase.Hovering;
        }

        _customMode = mode;
        return MavResult.Accepted;
    }

    // Mission upload, vehicle side: like a real autopilot, ask for every item in order and acknowledge at the end.
    private IMavlinkMessage StartMissionUpload(MissionCountMessage count, byte gcsSystem, byte gcsComponent)
    {
        _incoming = new List<MissionItemIntMessage>(count.Count);
        _incomingCount = count.Count;
        if (count.Count == 0)
        {
            _mission = [];
            return new MissionAckMessage(gcsSystem, gcsComponent, MavMissionResult.Accepted);
        }

        return new MissionRequestIntMessage(gcsSystem, gcsComponent, 0);
    }

    private IMavlinkMessage? ReceiveMissionItem(MissionItemIntMessage item, byte gcsSystem, byte gcsComponent)
    {
        if (_incoming is null)
        {
            return null; // no upload in progress
        }

        if (item.Seq != _incoming.Count)
        {
            // Out of order (a request or item was lost): ask again for the one we need.
            return new MissionRequestIntMessage(gcsSystem, gcsComponent, (ushort)_incoming.Count);
        }

        _incoming.Add(item);
        if (_incoming.Count < _incomingCount)
        {
            return new MissionRequestIntMessage(gcsSystem, gcsComponent, (ushort)_incoming.Count);
        }

        _mission = _incoming;
        _incoming = null;
        return new MissionAckMessage(gcsSystem, gcsComponent, MavMissionResult.Accepted);
    }
}
