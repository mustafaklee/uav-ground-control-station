namespace Gcs.Persistence.Telemetry;

/// <summary>
/// One row of telemetry history. Deliberately flat (one column per value, nullable when the vehicle had not reported it):
/// efficient to insert in bulk and easy to query for charts and flight replay.
/// </summary>
internal sealed class TelemetrySampleRecord
{
    public long Id { get; init; }

    public Guid VehicleId { get; init; }

    public DateTimeOffset RecordedAt { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public double? AltitudeMsl { get; init; }

    public double? RelativeAltitude { get; init; }

    public double? Roll { get; init; }

    public double? Pitch { get; init; }

    public double? Yaw { get; init; }

    public double? GroundSpeed { get; init; }

    public double? AirSpeed { get; init; }

    public double? ClimbRate { get; init; }

    public double? Heading { get; init; }

    public double? BatteryVoltage { get; init; }

    public double? BatteryCurrent { get; init; }

    public int? BatteryRemainingPercent { get; init; }

    public string? GpsFix { get; init; }

    public int? SatellitesVisible { get; init; }

    public bool? Armed { get; init; }

    public string? FlightMode { get; init; }
}
