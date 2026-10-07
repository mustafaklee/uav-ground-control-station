using Gcs.Domain.Vehicles;

namespace Gcs.Domain.Telemetry;

// Telemetry value objects in SI units and degrees, independent of MAVLink's wire units (1e-7 deg, mm, cm/s, centidegrees).
// The MAVLink layer converts once; everything above it works with these types.

/// <summary>Position on the WGS84 ellipsoid. Altitudes in metres: above mean sea level and above the home position.</summary>
public sealed record GeoPosition(double Latitude, double Longitude, double AltitudeMsl, double RelativeAltitude);

/// <summary>Orientation in degrees. Yaw 0 = north, positive clockwise.</summary>
public sealed record AttitudeAngles(double Roll, double Pitch, double Yaw);

/// <summary>
/// Speeds in m/s (climb positive upwards), heading in degrees 0–360. Air speed is null when the vehicle has no air speed
/// sensor (PX4 multicopters report NaN).
/// </summary>
public sealed record MotionState(double GroundSpeed, double? AirSpeed, double ClimbRate, double Heading);

/// <summary>Main battery. Each value is null when the vehicle reports it as unknown.</summary>
public sealed record BatteryState(double? Voltage, double? Current, int? RemainingPercent);

public enum GpsFix
{
    None = 0,
    NoFix = 1,
    Fix2D = 2,
    Fix3D = 3,
    Dgps = 4,
    RtkFloat = 5,
    RtkFixed = 6,
}

public sealed record GpsState(GpsFix Fix, int SatellitesVisible);

/// <summary>Arm state and flight mode, decoded from the vehicle's heartbeat (mode names differ per autopilot).</summary>
public sealed record FlightState(bool Armed, string FlightMode);

/// <summary>
/// One telemetry message's worth of new information. Most MAVLink messages carry only one aspect (position, attitude,
/// battery...), so an update sets the parts it knows and leaves the rest null.
/// </summary>
public sealed record TelemetryUpdate(
    DateTimeOffset ReceivedAt,
    GeoPosition? Position = null,
    AttitudeAngles? Attitude = null,
    MotionState? Motion = null,
    BatteryState? Battery = null,
    GpsState? Gps = null,
    FlightState? Flight = null);

/// <summary>
/// Latest known state of a vehicle, built by merging updates. This is the "live" view operators watch; it is kept in
/// memory and never written to the database message by message (historical telemetry is sampled separately, Phase 4).
/// </summary>
public sealed record TelemetrySnapshot(
    VehicleId VehicleId,
    DateTimeOffset UpdatedAt,
    GeoPosition? Position,
    AttitudeAngles? Attitude,
    MotionState? Motion,
    BatteryState? Battery,
    GpsState? Gps,
    FlightState? Flight)
{
    public static TelemetrySnapshot Empty(VehicleId vehicleId, DateTimeOffset at) => new(vehicleId, at, null, null, null, null, null, null);

    public TelemetrySnapshot Apply(TelemetryUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return this with
        {
            UpdatedAt = update.ReceivedAt > UpdatedAt ? update.ReceivedAt : UpdatedAt,
            Position = update.Position ?? Position,
            Attitude = update.Attitude ?? Attitude,
            Motion = update.Motion ?? Motion,
            Battery = update.Battery ?? Battery,
            Gps = update.Gps ?? Gps,
            Flight = update.Flight ?? Flight,
        };
    }
}
