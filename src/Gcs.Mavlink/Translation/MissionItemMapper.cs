using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Translation;

/// <summary>
/// Converts between domain mission items and MAVLink MISSION_ITEM_INT messages, including the autopilot differences:
/// <list type="bullet">
/// <item>ArduPilot reserves sequence 0 for the home position (it overwrites whatever is sent there), so a placeholder is
/// prepended on upload and dropped on download. PX4 uses sequence 0 for the first real item.</item>
/// <item>A takeoff or land without a position means "here". MAVLink has no "unset" value for the integer coordinates
/// of MISSION_ITEM_INT, and PX4 takes 0/0 literally: it would fly towards 0° N 0° E (found with PX4 SITL in Phase 10).
/// So "here" is resolved on upload: a takeoff gets the vehicle's position, a land the position of the item before it
/// (where the vehicle will be by then), or the vehicle's position when nothing before it has one.</item>
/// <item>Speed is not part of a waypoint in MAVLink; a DO_CHANGE_SPEED item is inserted before the first item that
/// changes it, and folded back into the following item on download.</item>
/// </list>
/// </summary>
public static class MissionItemMapper
{
    private const double E7 = 1e7;
    private const float SpeedTypeGround = 1;
    private const float ThrottleUnchanged = -1;
    private const byte AutoContinue = 1;

    /// <summary>"Unset" for parameters such as yaw: the autopilot keeps its own choice.</summary>
    private static readonly float Unset = float.NaN;

    /// <summary>True when an item means "here" and resolving it needs the vehicle's position.</summary>
    public static bool NeedsVehiclePosition(IReadOnlyList<MissionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            if (item.HasPosition)
            {
                return false; // every later "here" resolves to this or a later position
            }

            if (item.Command is MissionCommand.Takeoff or MissionCommand.Land)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the upload. <paramref name="vehiclePosition"/> (1e-7 degrees) is where the vehicle is now, used to resolve "here".</summary>
    public static IReadOnlyList<MissionItemIntMessage> ToMavlink(
        IReadOnlyList<MissionItem> items,
        AutopilotType autopilot,
        byte targetSystem,
        byte targetComponent,
        (int LatitudeE7, int LongitudeE7)? vehiclePosition = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var messages = new List<MissionItemIntMessage>();
        if (autopilot == AutopilotType.ArduPilot)
        {
            messages.Add(Item(MavCmd.NavWaypoint, MavFrame.GlobalRelativeAltInt, 0, 0, 0, 0, 0, 0, 0));
        }

        double? currentSpeed = null;
        var here = vehiclePosition ?? (0, 0);
        foreach (var item in items)
        {
            if (item.Speed is { } speed && speed != currentSpeed)
            {
                messages.Add(Item(MavCmd.DoChangeSpeed, MavFrame.Mission, SpeedTypeGround, (float)speed, ThrottleUnchanged, 0, 0, 0, 0));
                currentSpeed = speed;
            }

            if (item.HasPosition)
            {
                here = ((int)Math.Round(item.Latitude!.Value * E7), (int)Math.Round(item.Longitude!.Value * E7));
            }

            messages.Add(ToMavlink(item, here));
        }

        return [.. messages.Select((m, seq) => m with
        {
            TargetSystem = targetSystem,
            TargetComponent = targetComponent,
            Seq = (ushort)seq,
            Current = (byte)(seq == 0 ? 1 : 0),
        })];
    }

    public static IReadOnlyList<MissionItem> FromMavlink(IReadOnlyList<MissionItemIntMessage> messages, AutopilotType autopilot)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var items = new List<MissionItem>();
        double? pendingSpeed = null;
        foreach (var message in messages.Skip(autopilot == AutopilotType.ArduPilot ? 1 : 0))
        {
            if (message.Command == MavCmd.DoChangeSpeed)
            {
                pendingSpeed = message.Param2;
                continue;
            }

            if (FromMavlink(message, pendingSpeed) is { } item)
            {
                items.Add(item);
                pendingSpeed = null;
            }
        }

        return items;
    }

    /// <summary><paramref name="here"/> is the item's own position, or where the vehicle will be when it reaches an item without one.</summary>
    private static MissionItemIntMessage ToMavlink(MissionItem item, (int X, int Y) here)
    {
        var (x, y) = here;
        var z = (float)(item.Altitude ?? 0);
        var hold = (float)(item.HoldSeconds ?? 0);
        return item.Command switch
        {
            MissionCommand.Takeoff => Item(MavCmd.NavTakeoff, MavFrame.GlobalRelativeAltInt, 0, 0, 0, Unset, x, y, z),
            MissionCommand.Waypoint => Item(MavCmd.NavWaypoint, MavFrame.GlobalRelativeAltInt, hold, 0, 0, Unset, x, y, z),
            MissionCommand.Loiter => Item(MavCmd.NavLoiterTime, MavFrame.GlobalRelativeAltInt, hold, 0, 0, Unset, x, y, z),
            MissionCommand.ReturnToLaunch => Item(MavCmd.NavReturnToLaunch, MavFrame.Mission, 0, 0, 0, 0, 0, 0, 0),
            MissionCommand.Land => Item(MavCmd.NavLand, MavFrame.GlobalRelativeAltInt, 0, 0, 0, Unset, x, y, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Command, "Unknown mission command."),
        };
    }

    private static MissionItem? FromMavlink(MissionItemIntMessage m, double? speed)
    {
        double? lat = m.X == 0 && m.Y == 0 ? null : m.X / E7;
        double? lon = m.X == 0 && m.Y == 0 ? null : m.Y / E7;
        double? hold = m.Param1 > 0 ? m.Param1 : null;
        var result = m.Command switch
        {
            MavCmd.NavTakeoff => MissionItem.Create(MissionCommand.Takeoff, lat, lon, m.Z, speed: speed),
            MavCmd.NavWaypoint => MissionItem.Create(MissionCommand.Waypoint, lat, lon, m.Z, hold, speed),
            MavCmd.NavLoiterTime => MissionItem.Create(MissionCommand.Loiter, lat, lon, m.Z, hold, speed),
            MavCmd.NavReturnToLaunch => MissionItem.Create(MissionCommand.ReturnToLaunch),
            MavCmd.NavLand => MissionItem.Create(MissionCommand.Land, lat, lon, speed: speed),
            _ => null, // commands this GCS does not plan with are skipped
        };
        return result is { IsSuccess: true } ok ? ok.Value : null;
    }

    private static MissionItemIntMessage Item(MavCmd command, MavFrame frame, float p1, float p2, float p3, float p4, int x, int y, float z) =>
        new(0, 0, 0, frame, command, 0, AutoContinue, p1, p2, p3, p4, x, y, z);
}
