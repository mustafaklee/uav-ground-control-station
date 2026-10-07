using Gcs.Application.Abstractions;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace Gcs.Persistence.Telemetry;

internal sealed class TelemetryHistoryStore(GcsDbContext db) : ITelemetryHistoryStore
{
    public async Task AppendAsync(IReadOnlyCollection<TelemetrySample> samples, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);

        // EF Core + Npgsql send the inserts as one batched command; change tracking is not needed afterwards.
        db.TelemetrySamples.AddRange(samples.Select(ToRecord));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<TelemetrySample>> QueryAsync(
        VehicleId vehicleId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken)
    {
        var records = await db.TelemetrySamples
            .AsNoTracking()
            .Where(s => s.VehicleId == vehicleId.Value && s.RecordedAt >= since && s.RecordedAt < until)
            .OrderBy(s => s.RecordedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return [.. records.Select(ToSample)];
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        db.TelemetrySamples.Where(s => s.RecordedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    private static TelemetrySampleRecord ToRecord(TelemetrySample s) => new()
    {
        VehicleId = s.VehicleId.Value,
        RecordedAt = s.RecordedAt,
        Latitude = s.Position?.Latitude,
        Longitude = s.Position?.Longitude,
        AltitudeMsl = s.Position?.AltitudeMsl,
        RelativeAltitude = s.Position?.RelativeAltitude,
        Roll = s.Attitude?.Roll,
        Pitch = s.Attitude?.Pitch,
        Yaw = s.Attitude?.Yaw,
        GroundSpeed = s.Motion?.GroundSpeed,
        AirSpeed = s.Motion?.AirSpeed,
        ClimbRate = s.Motion?.ClimbRate,
        Heading = s.Motion?.Heading,
        BatteryVoltage = s.Battery?.Voltage,
        BatteryCurrent = s.Battery?.Current,
        BatteryRemainingPercent = s.Battery?.RemainingPercent,
        GpsFix = s.Gps?.Fix.ToString(),
        SatellitesVisible = s.Gps?.SatellitesVisible,
        Armed = s.Flight?.Armed,
        FlightMode = s.Flight?.FlightMode,
    };

    private static TelemetrySample ToSample(TelemetrySampleRecord r) => new(
        new VehicleId(r.VehicleId),
        r.RecordedAt,
        r is { Latitude: { } lat, Longitude: { } lon } ? new GeoPosition(lat, lon, r.AltitudeMsl ?? 0, r.RelativeAltitude ?? 0) : null,
        r is { Roll: { } roll, Pitch: { } pitch, Yaw: { } yaw } ? new AttitudeAngles(roll, pitch, yaw) : null,
        r is { GroundSpeed: { } gs } ? new MotionState(gs, r.AirSpeed ?? 0, r.ClimbRate ?? 0, r.Heading ?? 0) : null,
        r.BatteryVoltage is not null || r.BatteryCurrent is not null || r.BatteryRemainingPercent is not null
            ? new BatteryState(r.BatteryVoltage, r.BatteryCurrent, r.BatteryRemainingPercent)
            : null,
        r is { GpsFix: { } fix } && Enum.TryParse<GpsFix>(fix, out var gpsFix) ? new GpsState(gpsFix, r.SatellitesVisible ?? 0) : null,
        r is { Armed: { } armed } ? new FlightState(armed, r.FlightMode ?? string.Empty) : null);
}
