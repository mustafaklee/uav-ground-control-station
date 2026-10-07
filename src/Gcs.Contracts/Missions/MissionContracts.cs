namespace Gcs.Contracts.Missions;

/// <summary>
/// One mission step. <c>Command</c>: Takeoff, Waypoint, Loiter, ReturnToLaunch, Land.
/// Altitude in metres above home, hold time in seconds, speed in m/s (null keeps the current speed).
/// </summary>
public sealed record MissionItemDto(
    string? Command,
    double? Latitude = null,
    double? Longitude = null,
    double? Altitude = null,
    double? HoldSeconds = null,
    double? Speed = null);

/// <summary>Body of <c>POST /api/v1/missions</c> and <c>PUT /api/v1/missions/{id}</c> (with <c>If-Match</c>).</summary>
public sealed record SaveMissionRequest(string? Name, IReadOnlyList<MissionItemDto>? Items);

/// <summary>A rule the mission does not satisfy yet. <c>ItemIndex</c> is null for whole-mission rules.</summary>
public sealed record MissionIssueDto(int? ItemIndex, string Code, string Message);

public sealed record MissionUploadDto(Guid VehicleId, bool Succeeded, DateTimeOffset At, string? Error);

public sealed record MissionResponse(
    Guid Id,
    string Name,
    string Status,
    IReadOnlyList<MissionItemDto> Items,
    IReadOnlyList<MissionIssueDto> Issues,
    bool IsFlyable,
    double TotalDistanceMetres,
    MissionUploadDto? LastUpload,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MissionSummaryResponse(
    Guid Id,
    string Name,
    string Status,
    int ItemCount,
    bool IsFlyable,
    double TotalDistanceMetres,
    MissionUploadDto? LastUpload,
    int Version,
    DateTimeOffset UpdatedAt);

/// <summary>Body of <c>POST /api/v1/missions/{id}/upload</c>.</summary>
public sealed record UploadMissionRequest(Guid VehicleId);

/// <summary>The mission currently stored on a vehicle (<c>GET /api/v1/vehicles/{id}/mission</c>).</summary>
public sealed record VehicleMissionResponse(Guid VehicleId, IReadOnlyList<MissionItemDto> Items);
