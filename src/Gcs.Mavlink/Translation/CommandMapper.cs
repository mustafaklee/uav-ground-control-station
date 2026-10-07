using Gcs.Domain.Commands;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Translation;

/// <summary>
/// Turns an operator command into the COMMAND_LONG an autopilot expects (https://mavlink.io/en/services/command.html).
/// <list type="bullet">
/// <item>ARM / DISARM: MAV_CMD_COMPONENT_ARM_DISARM, param1 = 1 / 0.</item>
/// <item>TAKEOFF: MAV_CMD_NAV_TAKEOFF, param7 = altitude. PX4 reads param7 as altitude above mean sea level, ArduPilot as
/// metres above home, so for PX4 the home altitude (from telemetry) is added. Lat/lon/yaw NaN = "where you are".</item>
/// <item>LAND: MAV_CMD_NAV_LAND at the current position. RTL: MAV_CMD_NAV_RETURN_TO_LAUNCH.</item>
/// <item>SET_MODE: MAV_CMD_DO_SET_MODE with the autopilot's custom mode numbers (see <see cref="FlightModeDecoder"/>).</item>
/// </list>
/// </summary>
public static class CommandMapper
{
    /// <summary>The flight stack that runs on the vehicle. The built-in simulator behaves like PX4.</summary>
    public static MavAutopilot ToMavAutopilot(AutopilotType autopilot) =>
        autopilot == AutopilotType.ArduPilot ? MavAutopilot.ArduPilotMega : MavAutopilot.Px4;

    public static MavType ToMavType(VehicleType type) => type switch
    {
        VehicleType.FixedWing => MavType.FixedWing,
        VehicleType.Vtol => MavType.VtolTiltrotor,
        _ => MavType.Quadrotor,
    };

    /// <summary>Null when the command cannot be expressed yet, with the reason in <paramref name="problem"/>.</summary>
    public static CommandLongMessage? ToCommandLong(
        VehicleCommand command,
        AutopilotType autopilot,
        VehicleType type,
        byte targetSystem,
        double? homeAltitudeMsl,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(command);
        problem = null;
        var target = new CommandLongMessage(targetSystem, MavComponent.Autopilot1, MavCmd.ComponentArmDisarm, 0);
        switch (command.Type)
        {
            case VehicleCommandType.Arm:
                return target with { Param1 = 1 };
            case VehicleCommandType.Disarm:
                return target with { Param1 = 0 };
            case VehicleCommandType.Land:
                return target with { Command = MavCmd.NavLand, Param4 = float.NaN, Param5 = float.NaN, Param6 = float.NaN };
            case VehicleCommandType.ReturnToLaunch:
                return target with { Command = MavCmd.NavReturnToLaunch };
            case VehicleCommandType.Takeoff:
                var altitude = command.Altitude!.Value;
                if (ToMavAutopilot(autopilot) == MavAutopilot.Px4)
                {
                    if (homeAltitudeMsl is not { } home)
                    {
                        problem = "The vehicle's home altitude is not known yet (no position telemetry received).";
                        return null;
                    }

                    altitude += home;
                }

                return target with
                {
                    Command = MavCmd.NavTakeoff, Param4 = float.NaN, Param5 = float.NaN, Param6 = float.NaN, Param7 = (float)altitude,
                };
            case VehicleCommandType.SetMode:
                if (!FlightModeDecoder.TryEncode(ToMavAutopilot(autopilot), ToMavType(type), command.Mode!, out var custom, out var sub))
                {
                    problem = $"{command.Mode} is not a flight mode of this autopilot.";
                    return null;
                }

                return target with
                {
                    Command = MavCmd.DoSetMode, Param1 = (float)MavBaseMode.CustomModeEnabled, Param2 = custom, Param3 = sub,
                };
            default:
                problem = $"Command {command.Type} is not supported.";
                return null;
        }
    }
}
