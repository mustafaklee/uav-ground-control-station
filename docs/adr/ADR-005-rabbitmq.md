# ADR-005: RabbitMQ for domain events only, via a transactional outbox

* Status: Accepted
* Date: 2026-10-06

## Context

Several reactions to domain events are asynchronous and must not slow down a request or the vehicle link:
audit logging, notifications, analytics, future separate services. Telemetry arrives at up to tens of messages per
second per vehicle and must reach operators with minimal latency.

## Decision

* Use **RabbitMQ** (image `rabbitmq:4-management-alpine`, client `RabbitMQ.Client` 7) for **domain and integration
  events** such as `VehicleConnected`, `VehicleDisconnected`, `MissionUploaded`, `MissionStarted`,
  `MissionCompleted`, `VehicleAlarmTriggered`.
* **The telemetry hot path stays in-process** (MAVLink → telemetry processing → latest state → SignalR). Individual
  telemetry messages are not published to the broker.
* Events are written to an **outbox table in the same PostgreSQL transaction** as the state change and published by
  a background dispatcher, so an event is never lost and never published for a change that was rolled back (Phase 2+).
* Delivery is at-least-once; consumers must be idempotent (events carry ids).

## Alternatives considered

* **Everything through the broker, including telemetry**: adds latency and makes flying depend on the broker at
  runtime, which is a safety problem.
* **Kafka**: better for high-volume streams and replay, heavier to operate. Worth revisiting if telemetry must be
  streamed to many external consumers.
* **In-process events only**: simplest, but no durability and no path to distributed consumers.

## Consequences

* A broker outage must not stop vehicle control or live telemetry; it only delays asynchronous side effects.
* Readiness reports RabbitMQ status; the connection is created lazily so the API still starts while the broker is down.
* An outbox table and dispatcher have to be built and tested.
