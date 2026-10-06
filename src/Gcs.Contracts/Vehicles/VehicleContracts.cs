namespace Gcs.Contracts.Vehicles;

/// <summary>
/// Body of <c>POST /api/v1/vehicles</c>. Enum-like fields are strings ("Px4", "Udp") so the JSON is readable
/// and adding a new value later does not break older clients that only display it.
/// </summary>
public sealed record RegisterVehicleRequest(
    string? Callsign,
    int MavlinkSystemId,
    string? Autopilot,
    string? Type,
    ConnectionSettingsDto? Connection) : IVehicleFields;

/// <summary>Body of <c>PUT /api/v1/vehicles/{id}</c>. The edited version is sent in the <c>If-Match</c> header.</summary>
public sealed record UpdateVehicleRequest(
    string? Callsign,
    int MavlinkSystemId,
    string? Autopilot,
    string? Type,
    ConnectionSettingsDto? Connection) : IVehicleFields;

/// <summary>
/// Transport specific fields: <c>Host</c> + <c>Port</c> for Udp/Tcp, <c>SerialPort</c> + <c>BaudRate</c> for Serial,
/// nothing for Simulator.
/// </summary>
public sealed record ConnectionSettingsDto(
    string? Transport,
    string? Host = null,
    int? Port = null,
    string? SerialPort = null,
    int? BaudRate = null);

public sealed record VehicleResponse(
    Guid Id,
    string Callsign,
    int MavlinkSystemId,
    string Autopilot,
    string Type,
    ConnectionSettingsDto Connection,
    string Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Query string of <c>GET /api/v1/vehicles</c>.</summary>
public sealed record VehicleListQuery(
    int Page = 1,
    int PageSize = VehicleListQuery.DefaultPageSize,
    string? Status = null,
    string? Search = null)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}
