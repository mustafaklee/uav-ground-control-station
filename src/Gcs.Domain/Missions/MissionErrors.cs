using Gcs.Domain.Common;

namespace Gcs.Domain.Missions;

public static class MissionErrors
{
    public static readonly Error NameLength = Error.Validation(
        "mission.name.length", $"Mission name must be between 1 and {Mission.MaxNameLength} characters.");

    public static readonly Error TooManyItems = Error.Validation(
        "mission.items.too_many", $"A mission may have at most {Mission.MaxItems} items.");

    public static readonly Error ItemCommand = Error.Validation(
        "mission.item.command", "Unknown mission command.");

    public static readonly Error ItemPositionIncomplete = Error.Validation(
        "mission.item.position_incomplete", "Latitude and longitude must be given together.");

    public static readonly Error ItemPositionRange = Error.Validation(
        "mission.item.position_range", "Latitude must be within ±90° and longitude within ±180°.");

    public static readonly Error ItemPositionRequired = Error.Validation(
        "mission.item.position_required", "Waypoint and loiter items need a position.");

    public static readonly Error ItemReturnHasNoPosition = Error.Validation(
        "mission.item.rtl_position", "Return to launch flies to the home position; it takes no position or altitude.");

    public static readonly Error ItemAltitudeRequired = Error.Validation(
        "mission.item.altitude_required", "Takeoff, waypoint and loiter items need an altitude.");

    public static readonly Error ItemAltitudeRange = Error.Validation(
        "mission.item.altitude_range",
        $"Altitude must be between {MissionItem.MinAltitude} and {MissionItem.MaxAltitude} m above home.");

    public static readonly Error ItemHold = Error.Validation(
        "mission.item.hold",
        $"Hold time must be between 0 and {MissionItem.MaxHoldSeconds} s; loiter items need a hold time above 0.");

    public static readonly Error ItemSpeed = Error.Validation(
        "mission.item.speed", $"Speed must be between {MissionItem.MinSpeed} and {MissionItem.MaxSpeed} m/s.");

    public static readonly Error NotFound = Error.NotFound("mission.not_found", "The mission does not exist.");

    public static readonly Error Archived = Error.Conflict("mission.archived", "The mission is archived and can no longer be changed.");

    public static readonly Error VersionMismatch = Error.ConcurrencyConflict(
        "mission.version_mismatch", "The mission was changed by someone else. Reload it and apply your change again.");

    public static readonly Error NotFlyable = Error.Conflict(
        "mission.not_flyable", "The mission has validation errors and cannot be uploaded.");
}
