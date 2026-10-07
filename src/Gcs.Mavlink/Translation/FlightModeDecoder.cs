using Gcs.Mavlink.Protocol;

namespace Gcs.Mavlink.Translation;

/// <summary>
/// Turns HEARTBEAT.custom_mode into a readable flight mode. The number means different things per autopilot:
/// PX4 packs a main mode and a sub mode into bytes 2 and 3; ArduPilot uses one number per vehicle family
/// (Copter "5" is LOITER, Plane "5" is FBWA). Keeping this here is what lets the rest of the GCS stay autopilot neutral.
/// </summary>
public static class FlightModeDecoder
{
    public const string Unknown = "UNKNOWN";

    private static readonly Dictionary<byte, string> Px4MainModes = new()
    {
        [1] = "MANUAL", [2] = "ALTCTL", [3] = "POSCTL", [4] = "AUTO", [5] = "ACRO", [6] = "OFFBOARD", [7] = "STABILIZED", [8] = "RATTITUDE",
    };

    private static readonly Dictionary<byte, string> Px4AutoSubModes = new()
    {
        [1] = "READY", [2] = "TAKEOFF", [3] = "LOITER", [4] = "MISSION", [5] = "RTL", [6] = "LAND", [8] = "FOLLOW_TARGET", [9] = "PRECLAND",
    };

    private static readonly Dictionary<uint, string> ArduCopterModes = new()
    {
        [0] = "STABILIZE", [1] = "ACRO", [2] = "ALT_HOLD", [3] = "AUTO", [4] = "GUIDED", [5] = "LOITER", [6] = "RTL",
        [7] = "CIRCLE", [9] = "LAND", [16] = "POSHOLD", [17] = "BRAKE", [21] = "SMART_RTL",
    };

    private static readonly Dictionary<uint, string> ArduPlaneModes = new()
    {
        [0] = "MANUAL", [1] = "CIRCLE", [2] = "STABILIZE", [5] = "FBWA", [6] = "FBWB", [10] = "AUTO", [11] = "RTL",
        [12] = "LOITER", [15] = "GUIDED", [17] = "QSTABILIZE", [18] = "QHOVER", [19] = "QLOITER", [20] = "QLAND", [21] = "QRTL",
    };

    public static string Decode(MavAutopilot autopilot, MavType type, MavBaseMode baseMode, uint customMode)
    {
        if (!baseMode.HasFlag(MavBaseMode.CustomModeEnabled))
        {
            return Unknown;
        }

        return autopilot switch
        {
            MavAutopilot.Px4 => DecodePx4(customMode),
            MavAutopilot.ArduPilotMega => Lookup(ArduPilotTable(type), customMode),
            _ => Unknown,
        };
    }

    /// <summary>
    /// PX4 modes an operator may select. OFFBOARD (companion computer control), READY and the follow/precision-land
    /// sub modes are left out: entering them from a GCS button is not a meaningful operator action.
    /// </summary>
    private static readonly string[] Px4SelectableModes =
    [
        "MANUAL", "STABILIZED", "ACRO", "ALTCTL", "POSCTL", "AUTO.TAKEOFF", "AUTO.LOITER", "AUTO.MISSION", "AUTO.RTL", "AUTO.LAND",
    ];

    /// <summary>Mode names SetMode accepts, the same strings <see cref="Decode"/> produces.</summary>
    public static IReadOnlyList<string> SelectableModes(MavAutopilot autopilot, MavType type) => autopilot switch
    {
        MavAutopilot.Px4 => Px4SelectableModes,
        MavAutopilot.ArduPilotMega => [.. ArduPilotTable(type).Values],
        _ => [],
    };

    /// <summary>
    /// The reverse of <see cref="Decode"/>, as MAV_CMD_DO_SET_MODE parameters: param1 = base mode (custom mode enabled),
    /// param2 = custom (main) mode, param3 = PX4 sub mode. Returns false for names this autopilot does not have.
    /// </summary>
    public static bool TryEncode(MavAutopilot autopilot, MavType type, string mode, out uint customMode, out uint subMode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        customMode = 0;
        subMode = 0;
        switch (autopilot)
        {
            case MavAutopilot.Px4:
                var parts = mode.Split('.', 2);
                var main = Px4MainModes.FirstOrDefault(m => m.Value == parts[0]);
                if (main.Value is null || (parts.Length == 2) != (main.Key == 4))
                {
                    return false; // unknown main mode, or AUTO without a sub mode / a sub mode on a non-AUTO mode
                }

                customMode = main.Key;
                if (parts.Length == 2)
                {
                    var sub = Px4AutoSubModes.FirstOrDefault(m => m.Value == parts[1]);
                    if (sub.Value is null)
                    {
                        return false;
                    }

                    subMode = sub.Key;
                }

                return true;
            case MavAutopilot.ArduPilotMega:
                var entry = ArduPilotTable(type).FirstOrDefault(m => m.Value == mode);
                customMode = entry.Key;
                return entry.Value is not null;
            default:
                return false;
        }
    }

    private static Dictionary<uint, string> ArduPilotTable(MavType type) =>
        type == MavType.FixedWing || IsVtol(type) ? ArduPlaneModes : ArduCopterModes;

    private static string DecodePx4(uint customMode)
    {
        var main = (byte)((customMode >> 16) & 0xFF);
        var sub = (byte)((customMode >> 24) & 0xFF);
        if (!Px4MainModes.TryGetValue(main, out var mainName))
        {
            return Unknown;
        }

        return main == 4 && Px4AutoSubModes.TryGetValue(sub, out var subName) ? $"{mainName}.{subName}" : mainName;
    }

    private static string Lookup(Dictionary<uint, string> table, uint mode) =>
        table.TryGetValue(mode, out var name) ? name : Unknown;

    private static bool IsVtol(MavType type) =>
        type is MavType.VtolTailsitterDuorotor or MavType.VtolTailsitterQuadrotor or MavType.VtolTiltrotor or MavType.VtolFixedrotor;
}
