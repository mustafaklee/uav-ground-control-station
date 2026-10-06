namespace Gcs.Contracts.Vehicles;

/// <summary>Fields shared by register and update requests, so both are validated by the same rules.</summary>
public interface IVehicleFields
{
    string? Callsign { get; }

    int MavlinkSystemId { get; }

    string? Autopilot { get; }

    string? Type { get; }

    ConnectionSettingsDto? Connection { get; }
}
