namespace Gcs.Contracts.Commands;

/// <summary>HTTP headers of the command API.</summary>
public static class CommandHeaders
{
    /// <summary>
    /// Who is acting (e.g. <c>operator01</c>). Required on every lease and command request until sign-in exists (Phase 8);
    /// then the authenticated user replaces it.
    /// </summary>
    public const string Operator = "X-Operator";
}

/// <summary>
/// Body of <c>POST /api/v1/vehicles/{id}/commands</c>.
/// <c>Command</c>: Arm, Disarm, Takeoff, Land, ReturnToLaunch, SetMode. <c>Altitude</c> (m above home) only for Takeoff,
/// <c>Mode</c> only for SetMode. Critical commands (Arm, Disarm, Takeoff, SetMode) need <c>Confirm = true</c>.
/// </summary>
public sealed record SendCommandRequest(string? Command, double? Altitude = null, string? Mode = null, bool Confirm = false);

/// <summary>
/// One audit log entry; also the answer to a command. <c>Outcome</c>: Pending, Accepted, Rejected, TimedOut, Refused.
/// </summary>
public sealed record CommandAuditResponse(
    Guid Id,
    Guid VehicleId,
    string Callsign,
    string Operator,
    string Command,
    string? Parameters,
    string Outcome,
    string? Detail,
    int Attempts,
    string Source,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Who controls a vehicle. <c>Holder</c> is null when nobody does.</summary>
public sealed record CommandLeaseResponse(Guid VehicleId, string? Holder, DateTimeOffset? AcquiredAt, DateTimeOffset? ExpiresAt);

/// <summary>Flight modes a vehicle accepts in SetMode, in the names the GCS displays.</summary>
public sealed record FlightModesResponse(Guid VehicleId, IReadOnlyList<string> Modes);
