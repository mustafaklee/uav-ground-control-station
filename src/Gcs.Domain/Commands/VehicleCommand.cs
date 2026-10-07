using Gcs.Domain.Common;

namespace Gcs.Domain.Commands;

/// <summary>What an operator can tell a vehicle to do right now. Autopilot neutral; the MAVLink layer maps it to MAV_CMD.</summary>
public enum VehicleCommandType
{
    /// <summary>Start the motors. The vehicle is live afterwards: propellers spin when it takes off.</summary>
    Arm = 1,

    /// <summary>Stop the motors. Autopilots refuse this in flight, because the vehicle would fall.</summary>
    Disarm = 2,

    /// <summary>Climb vertically to <see cref="VehicleCommand.Altitude"/> metres above home.</summary>
    Takeoff = 3,

    /// <summary>Land where the vehicle is.</summary>
    Land = 4,

    /// <summary>Fly back to the home position and land.</summary>
    ReturnToLaunch = 5,

    /// <summary>Switch the flight mode to <see cref="VehicleCommand.Mode"/> (e.g. AUTO.LOITER, POSCTL).</summary>
    SetMode = 6,
}

/// <summary>
/// One command with its parameters, validated on creation. A value object: two "Takeoff to 30 m" commands are equal,
/// which is exactly what the duplicate check in the link layer compares.
/// </summary>
public sealed record VehicleCommand
{
    public const double MinTakeoffAltitude = 2;
    public const double MaxTakeoffAltitude = 500;
    public const int MaxModeLength = 32;

    private VehicleCommand(VehicleCommandType type, double? altitude, string? mode)
    {
        Type = type;
        Altitude = altitude;
        Mode = mode;
    }

    public VehicleCommandType Type { get; }

    /// <summary>Takeoff altitude in metres above the home position. Only for <see cref="VehicleCommandType.Takeoff"/>.</summary>
    public double? Altitude { get; }

    /// <summary>Flight mode name as the GCS displays it (upper case). Only for <see cref="VehicleCommandType.SetMode"/>.</summary>
    public string? Mode { get; }

    /// <summary>
    /// Commands that start motors, make the vehicle leave the ground, cut the motors or change who flies it. The operator
    /// must confirm them explicitly. LAND and RTL are not here: they are what an operator sends when something goes
    /// wrong, and an extra dialog would only slow down the safe reaction.
    /// </summary>
    public bool RequiresConfirmation => Type is VehicleCommandType.Arm or VehicleCommandType.Disarm
        or VehicleCommandType.Takeoff or VehicleCommandType.SetMode;

    /// <summary>Short parameter text for the audit log, e.g. "altitude=30" or "mode=AUTO.LOITER".</summary>
    public string? DescribeParameters() => Type switch
    {
        VehicleCommandType.Takeoff => FormattableString.Invariant($"altitude={Altitude}"),
        VehicleCommandType.SetMode => $"mode={Mode}",
        _ => null,
    };

    public static VehicleCommand Arm() => new(VehicleCommandType.Arm, null, null);

    public static VehicleCommand Disarm() => new(VehicleCommandType.Disarm, null, null);

    public static VehicleCommand Land() => new(VehicleCommandType.Land, null, null);

    public static VehicleCommand ReturnToLaunch() => new(VehicleCommandType.ReturnToLaunch, null, null);

    public static Result<VehicleCommand> Takeoff(double? altitude) =>
        altitude is { } value && double.IsFinite(value) && value is >= MinTakeoffAltitude and <= MaxTakeoffAltitude
            ? new VehicleCommand(VehicleCommandType.Takeoff, value, null)
            : CommandErrors.TakeoffAltitude;

    public static Result<VehicleCommand> SetMode(string? mode)
    {
        var normalized = mode?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length is 0 or > MaxModeLength
            ? CommandErrors.ModeRequired
            : new VehicleCommand(VehicleCommandType.SetMode, null, normalized);
    }

    /// <summary>Builds a command from API input. Parameters that do not belong to the command are rejected, not ignored.</summary>
    public static Result<VehicleCommand> Create(VehicleCommandType type, double? altitude, string? mode)
    {
        if ((altitude is not null && type != VehicleCommandType.Takeoff) || (mode is not null && type != VehicleCommandType.SetMode))
        {
            return CommandErrors.UnexpectedParameter;
        }

        return type switch
        {
            VehicleCommandType.Arm => Arm(),
            VehicleCommandType.Disarm => Disarm(),
            VehicleCommandType.Land => Land(),
            VehicleCommandType.ReturnToLaunch => ReturnToLaunch(),
            VehicleCommandType.Takeoff => Takeoff(altitude),
            VehicleCommandType.SetMode => SetMode(mode),
            _ => CommandErrors.UnknownCommand,
        };
    }
}
