# ADR-003: Modular monolith with Clean Architecture

* Status: Accepted
* Date: 2026-10-06

## Context

The system has several clear areas (vehicles, MAVLink, telemetry, missions, commands, security, audit) but one small
team, one deployment target and hard latency requirements on the telemetry path.

## Decision

Build a **single deployable backend** split into projects following **Clean Architecture**: Domain at the centre,
Application defining use cases and ports, adapters (Persistence, Messaging, Mavlink, Telemetry) implementing those
ports, Infrastructure as the composition root, and Api as the host. The dependency rules are enforced by
`tests/Gcs.ArchitectureTests`. DDD tactical patterns (aggregates, value objects, domain events) are used where they
protect real invariants, not everywhere.

## Alternatives considered

* **Microservices from day one**: independent scaling and deployment, but network hops on the telemetry path,
  distributed transactions, and far more operational work than the problem needs today.
* **Single project, or layers by technical type only**: fastest start, but boundaries erode and MAVLink details leak into the UI and API.

## Consequences

* In-process calls on hot paths; one database, one deployment.
* Module boundaries plus domain events over RabbitMQ (ADR-005) keep a later extraction into services possible.
* Architecture tests must be kept up to date when projects are added.
