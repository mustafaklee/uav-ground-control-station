using System.Globalization;
using Gcs.Domain.Common;

namespace Gcs.Domain.Commands;

public static class CommandErrors
{
    public static readonly Error UnknownCommand = Error.Validation(
        "command.unknown", "Unknown command. Use one of: Arm, Disarm, Takeoff, Land, ReturnToLaunch, SetMode.");

    public static readonly Error UnexpectedParameter = Error.Validation(
        "command.parameter_unexpected", "Altitude belongs only to Takeoff and mode only to SetMode.");

    public static readonly Error TakeoffAltitude = Error.Validation(
        "command.takeoff.altitude",
        $"Takeoff needs an altitude between {VehicleCommand.MinTakeoffAltitude} and {VehicleCommand.MaxTakeoffAltitude} m above home.");

    public static readonly Error ModeRequired = Error.Validation(
        "command.mode.required", $"SetMode needs a flight mode name of at most {VehicleCommand.MaxModeLength} characters.");

    public static readonly Error ConfirmationRequired = Error.Validation(
        "command.confirmation_required",
        "This command is critical (it starts or stops motors, or makes the vehicle fly). Send it again with \"confirm\": true.");

    public static readonly Error OperatorRequired = Error.Validation(
        "command.operator_required",
        $"Send who is acting in the X-Operator header: 1 to {OperatorName.MaxLength} letters, digits or . _ @ -.");

    public static readonly Error LeaseRequired = Error.Conflict(
        "command.lease_required", "Take control of the vehicle (acquire its command lease) before sending commands.");

    public static readonly Error AlreadyInFlight = Error.Conflict(
        "command.in_flight",
        "The same command is already on its way to this vehicle. Wait for its answer instead of sending it again.");

    public static readonly Error NotConnected = Error.Conflict(
        "command.not_connected", "The vehicle is not connected, so the command could not be sent.");

    public static readonly Error Unsupported = Error.Conflict(
        "command.unsupported", "The command cannot be sent to this vehicle.");

    public static readonly Error Rejected = Error.Conflict(
        "command.rejected", "The vehicle refused the command.");

    public static readonly Error TimedOut = Error.Timeout(
        "command.timed_out", "The vehicle did not acknowledge the command in time. Check its state before trying again.");

    public static Error UnknownMode(IEnumerable<string> available) => Error.Validation(
        "command.mode.unknown", $"Unknown flight mode for this vehicle. Available: {string.Join(", ", available)}.");

    public static Error LeaseHeldByOther(OperatorName holder, DateTimeOffset expiresAt) => Error.Conflict(
        "command.lease_held",
        string.Create(CultureInfo.InvariantCulture, $"{holder} controls this vehicle until {expiresAt:HH:mm:ss} UTC (unless renewed)."));
}
