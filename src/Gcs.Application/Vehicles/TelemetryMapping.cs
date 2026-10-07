using Gcs.Application.Abstractions;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Telemetry;

namespace Gcs.Application.Vehicles;

/// <summary>Domain telemetry → API contract. Used by the REST endpoints, the live broadcaster and the history query.</summary>
public static class TelemetryMapping
{
    public static TelemetryResponse ToResponse(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Map(snapshot.VehicleId.Value, snapshot.UpdatedAt, snapshot.Position, snapshot.Attitude, snapshot.Motion,
            snapshot.Battery, snapshot.Gps, snapshot.Flight);
    }

    public static TelemetryResponse ToResponse(TelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Map(sample.VehicleId.Value, sample.RecordedAt, sample.Position, sample.Attitude, sample.Motion,
            sample.Battery, sample.Gps, sample.Flight);
    }

    private static TelemetryResponse Map(
        Guid vehicleId,
        DateTimeOffset at,
        GeoPosition? position,
        AttitudeAngles? attitude,
        MotionState? motion,
        BatteryState? battery,
        GpsState? gps,
        FlightState? flight) => new(
        vehicleId,
        at,
        position is { } p ? new PositionDto(p.Latitude, p.Longitude, p.AltitudeMsl, p.RelativeAltitude) : null,
        attitude is { } a ? new AttitudeDto(a.Roll, a.Pitch, a.Yaw) : null,
        motion is { } m ? new MotionDto(m.GroundSpeed, m.AirSpeed, m.ClimbRate, m.Heading) : null,
        battery is { } b ? new BatteryDto(b.Voltage, b.Current, b.RemainingPercent) : null,
        gps is { } g ? new GpsDto(g.Fix.ToString(), g.SatellitesVisible) : null,
        flight is { } f ? new FlightDto(f.Armed, f.FlightMode) : null);
}
