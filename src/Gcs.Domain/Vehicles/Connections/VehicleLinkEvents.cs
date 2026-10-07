using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles.Connections;

// Link lifecycle events. They are published to RabbitMQ through the outbox (ADR-005), so other parts of the system
// (audit, alarms, notifications) can react without the MAVLink layer knowing about them.

/// <summary>The first heartbeat arrived, or the link recovered after a loss.</summary>
public sealed record VehicleConnected(VehicleId VehicleId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Heartbeats stopped while connected; the link is trying to recover.</summary>
public sealed record VehicleLinkLost(VehicleId VehicleId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Connecting or reconnecting gave up. Operator attention required.</summary>
public sealed record VehicleLinkFaulted(VehicleId VehicleId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The link was closed on purpose (operator disconnect or vehicle retired).</summary>
public sealed record VehicleDisconnected(VehicleId VehicleId, DateTimeOffset OccurredAt) : IDomainEvent;
