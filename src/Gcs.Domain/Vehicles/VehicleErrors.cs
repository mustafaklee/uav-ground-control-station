using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles;

/// <summary>
/// Every expected vehicle failure in one place, with a stable machine readable code
/// that API clients can rely on (the message may change, the code does not).
/// </summary>
public static class VehicleErrors
{
    public static readonly Error CallsignLength = Error.Validation(
        "vehicle.callsign.length",
        $"Callsign must be between {Callsign.MinLength} and {Callsign.MaxLength} characters.");

    public static readonly Error CallsignFormat = Error.Validation(
        "vehicle.callsign.format",
        "Callsign may contain only letters, digits and single hyphens between them (e.g. UAV-01).");

    public static readonly Error SystemIdRange = Error.Validation(
        "vehicle.system_id.range",
        $"MAVLink system id must be between {MavlinkSystemId.Min} and {MavlinkSystemId.Max}.");

    public static readonly Error Host = Error.Validation(
        "vehicle.connection.host",
        $"Host is required, must not contain spaces and must be at most {ConnectionSettings.MaxHostLength} characters.");

    public static readonly Error Port = Error.Validation(
        "vehicle.connection.port",
        $"Port must be between {ConnectionSettings.MinPort} and {ConnectionSettings.MaxPort}.");

    public static readonly Error SerialPortName = Error.Validation(
        "vehicle.connection.serial_port",
        $"Serial port name is required and must be at most {ConnectionSettings.MaxSerialPortNameLength} characters.");

    public static readonly Error BaudRate = Error.Validation(
        "vehicle.connection.baud_rate",
        $"Baud rate must be one of: {string.Join(", ", ConnectionSettings.SupportedBaudRates.Order())}.");

    public static readonly Error Retired = Error.Conflict(
        "vehicle.retired",
        "The vehicle is retired and can no longer be changed.");

    public static readonly Error NotFound = Error.NotFound(
        "vehicle.not_found",
        "The vehicle does not exist.");

    public static readonly Error CallsignInUse = Error.Conflict(
        "vehicle.callsign.in_use",
        "Another active vehicle already uses this callsign.");

    public static readonly Error SystemIdInUse = Error.Conflict(
        "vehicle.system_id.in_use",
        "Another active vehicle already uses this MAVLink system id.");

    public static readonly Error VersionMismatch = Error.ConcurrencyConflict(
        "vehicle.version_mismatch",
        "The vehicle was changed by someone else. Reload it and apply your change again.");
}
