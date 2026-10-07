using Gcs.Domain.Common;

namespace Gcs.Domain.Missions;

public readonly record struct MissionId(Guid Value)
{
    public static MissionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>What a mission step makes the vehicle do. Autopilot neutral; the MAVLink layer maps it to MAV_CMD.</summary>
public enum MissionCommand
{
    /// <summary>Climb to <see cref="MissionItem.Altitude"/> (optionally at a position).</summary>
    Takeoff = 1,

    /// <summary>Fly to a position, optionally hold there and/or change speed.</summary>
    Waypoint = 2,

    /// <summary>Fly to a position and circle/hover there for <see cref="MissionItem.HoldSeconds"/>.</summary>
    Loiter = 3,

    /// <summary>Return to the home position (and land, depending on autopilot settings).</summary>
    ReturnToLaunch = 4,

    /// <summary>Land at a position, or where the vehicle is when no position is given.</summary>
    Land = 5,
}

/// <summary>
/// One mission step. A value object: validated on creation, never changed afterwards. Altitudes are metres above the
/// home position (the MAVLink "relative altitude" frame), which is what operators plan with.
/// </summary>
public sealed record MissionItem
{
    public const double MinAltitude = 2;
    public const double MaxAltitude = 500;
    public const double MaxHoldSeconds = 3600;
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 40;

    private MissionItem(MissionCommand command, double? latitude, double? longitude, double? altitude, double? holdSeconds, double? speed)
    {
        Command = command;
        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
        HoldSeconds = holdSeconds;
        Speed = speed;
    }

    public MissionCommand Command { get; }

    public double? Latitude { get; }

    public double? Longitude { get; }

    /// <summary>Metres above home.</summary>
    public double? Altitude { get; }

    public double? HoldSeconds { get; }

    /// <summary>Ground speed in m/s to use from this item on; null keeps the current speed.</summary>
    public double? Speed { get; }

    public bool HasPosition => Latitude is not null && Longitude is not null;

    public static Result<MissionItem> Create(
        MissionCommand command,
        double? latitude = null,
        double? longitude = null,
        double? altitude = null,
        double? holdSeconds = null,
        double? speed = null)
    {
        if (!Enum.IsDefined(command))
        {
            return MissionErrors.ItemCommand;
        }

        if ((latitude is null) != (longitude is null))
        {
            return MissionErrors.ItemPositionIncomplete;
        }

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return MissionErrors.ItemPositionRange;
        }

        var needsPosition = command is MissionCommand.Waypoint or MissionCommand.Loiter;
        if (needsPosition && latitude is null)
        {
            return MissionErrors.ItemPositionRequired;
        }

        if (command == MissionCommand.ReturnToLaunch && (latitude is not null || altitude is not null))
        {
            return MissionErrors.ItemReturnHasNoPosition;
        }

        var needsAltitude = command is MissionCommand.Takeoff or MissionCommand.Waypoint or MissionCommand.Loiter;
        if (needsAltitude && altitude is null)
        {
            return MissionErrors.ItemAltitudeRequired;
        }

        if (altitude is < MinAltitude or > MaxAltitude)
        {
            return MissionErrors.ItemAltitudeRange;
        }

        if (holdSeconds is < 0 or > MaxHoldSeconds || (command == MissionCommand.Loiter && holdSeconds is null or 0))
        {
            return MissionErrors.ItemHold;
        }

        if (speed is < MinSpeed or > MaxSpeed)
        {
            return MissionErrors.ItemSpeed;
        }

        return new MissionItem(command, latitude, longitude, altitude, holdSeconds, speed);
    }
}
