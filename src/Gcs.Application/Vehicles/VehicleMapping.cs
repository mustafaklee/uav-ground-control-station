using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>
/// Translation between API contracts and the domain. Domain types never leave the application layer;
/// the API only ever sees DTOs, so the domain can change without breaking clients.
/// </summary>
public static class VehicleMapping
{
    public static VehicleResponse ToResponse(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return new VehicleResponse(
            vehicle.Id.Value,
            vehicle.Callsign.Value,
            vehicle.SystemId.Value,
            vehicle.Autopilot.ToString(),
            vehicle.Type.ToString(),
            ToDto(vehicle.Connection),
            vehicle.Status.ToString(),
            vehicle.Version,
            vehicle.CreatedAt,
            vehicle.UpdatedAt);
    }

    public static ConnectionSettingsDto ToDto(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ConnectionSettingsDto(
            settings.Transport.ToString(),
            settings.Host,
            settings.Port,
            settings.SerialPortName,
            settings.BaudRate);
    }

    public static Result<ConnectionSettings> ToConnectionSettings(ConnectionSettingsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!TryParseEnum<TransportType>(dto.Transport, out var transport))
        {
            return Error.Validation("vehicle.connection.transport", "Transport must be one of: Udp, Tcp, Serial, Simulator.");
        }

        return transport switch
        {
            TransportType.Udp => ConnectionSettings.Udp(dto.Host, dto.Port ?? 0),
            TransportType.Tcp => ConnectionSettings.Tcp(dto.Host, dto.Port ?? 0),
            TransportType.Serial => ConnectionSettings.Serial(dto.SerialPort, dto.BaudRate ?? 0),
            TransportType.Simulator => ConnectionSettings.Simulator(),
            _ => throw new ArgumentOutOfRangeException(nameof(dto), transport, "Unhandled transport type."),
        };
    }

    /// <summary>
    /// Parses an enum by name only, ignoring case. <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> on its own
    /// would also accept numbers like "7", which would let undefined values into the domain.
    /// </summary>
    public static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value)
            && char.IsLetter(value.Trim()[0])
            && Enum.TryParse(value.Trim(), ignoreCase: true, out result)
            && Enum.IsDefined(result);
    }

    internal static VehicleInput ToInput(IVehicleFields fields)
    {
        // Only called after VehicleFieldsValidator succeeded, so every conversion below must succeed.
        // A failure here is a bug (validator and mapping out of sync), not bad input, hence the exception.
        if (!TryParseEnum<AutopilotType>(fields.Autopilot, out var autopilot)
            || !TryParseEnum<VehicleType>(fields.Type, out var type))
        {
            throw new InvalidOperationException("Vehicle fields must be validated before they are mapped.");
        }

        return new VehicleInput(
            Callsign.Create(fields.Callsign).Value,
            MavlinkSystemId.Create(fields.MavlinkSystemId).Value,
            autopilot,
            type,
            ToConnectionSettings(fields.Connection!).Value);
    }
}

internal sealed record VehicleInput(
    Callsign Callsign,
    MavlinkSystemId SystemId,
    AutopilotType Autopilot,
    VehicleType Type,
    ConnectionSettings Connection);
