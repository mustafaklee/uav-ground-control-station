using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles;

public sealed record VehicleRegistered(
    VehicleId VehicleId,
    Callsign Callsign,
    MavlinkSystemId SystemId,
    AutopilotType Autopilot,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record VehicleUpdated(
    VehicleId VehicleId,
    Callsign Callsign,
    int Version,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record VehicleRetired(
    VehicleId VehicleId,
    Callsign Callsign,
    DateTimeOffset OccurredAt) : IDomainEvent;
