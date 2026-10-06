namespace Gcs.Contracts.Health;

/// <summary>
/// Public shape of the readiness/liveness endpoints. Kept separate from ASP.NET Core's HealthReport
/// so the wire format stays stable even if the framework type changes.
/// </summary>
public sealed record HealthReportResponse(
    string Status,
    double TotalDurationMs,
    IReadOnlyList<HealthCheckEntryResponse> Checks);

public sealed record HealthCheckEntryResponse(
    string Name,
    string Status,
    double DurationMs,
    string? Description);
