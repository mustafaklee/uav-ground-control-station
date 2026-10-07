namespace Gcs.Contracts.Vehicles;

/// <summary>
/// Live link status. <c>State</c> is one of Disconnected, Connecting, Connected, Reconnecting, Faulted.
/// </summary>
public sealed record VehicleLinkResponse(
    Guid VehicleId,
    string State,
    DateTimeOffset? LastHeartbeatAt,
    int ReconnectAttempts,
    string? FaultReason,
    LinkQualityDto Quality);

public sealed record LinkQualityDto(long FramesReceived, long FramesLost, double PacketLossRatio, long CrcErrors);

/// <summary>Latest telemetry of a vehicle. Parts the vehicle has not reported yet are null.</summary>
public sealed record TelemetryResponse(
    Guid VehicleId,
    DateTimeOffset UpdatedAt,
    PositionDto? Position,
    AttitudeDto? Attitude,
    MotionDto? Motion,
    BatteryDto? Battery,
    GpsDto? Gps,
    FlightDto? Flight);

/// <summary>Degrees (WGS84) and metres: above mean sea level and above home.</summary>
public sealed record PositionDto(double Latitude, double Longitude, double AltitudeMsl, double RelativeAltitude);

/// <summary>Degrees.</summary>
public sealed record AttitudeDto(double Roll, double Pitch, double Yaw);

/// <summary>m/s and degrees.</summary>
public sealed record MotionDto(double GroundSpeed, double AirSpeed, double ClimbRate, double Heading);

/// <summary>Volts, amperes, percent.</summary>
public sealed record BatteryDto(double? Voltage, double? Current, int? RemainingPercent);

public sealed record GpsDto(string Fix, int SatellitesVisible);

public sealed record FlightDto(bool Armed, string FlightMode);
